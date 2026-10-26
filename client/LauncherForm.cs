using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Chispa.Launcher;

internal sealed class LauncherForm : Form
{
    private readonly LauncherApi api;
    private readonly Installer installer;
    private readonly GlassCanvas loginPage = new();
    private readonly GlassCanvas libraryPage = new();
    private readonly TextBox username = new();
    private readonly TextBox password = new();
    private readonly GlassCheck remember = new();
    private readonly FlowLayoutPanel programCards = new();
    private readonly Label loginStatus = new();
    private readonly Label libraryStatus = new();
    private readonly Label programName = new();
    private readonly Label version = new();
    private readonly Label welcome = new();
    private readonly Label catalogCount = new();
    private readonly GlassProgress progress = new();
    private readonly GlassButton logout = new();
    private readonly GlassCheck showPassword = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly GlassButton login = new();
    private readonly GlassButton testConnection = new();
    private readonly GlassButton syncCatalog = new();
    private readonly GlassButton refresh = new();
    private readonly GlassButton install = new();
    private readonly GlassButton launch = new();
    private ProgramInfo? selected;
    private Release? latest;
    private bool busy;
    private readonly System.Windows.Forms.Timer catalogTimer = new() { Interval = 60_000 };

    private static readonly Color Background = GlassTheme.Background;
    private static readonly Color Accent = GlassTheme.Accent;
    private static readonly Color Muted = GlassTheme.Muted;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW for a borderless window
            return parameters;
        }
    }

    public LauncherForm(LauncherApi api, Installer installer)
    {
        this.api = api;
        this.installer = installer;
        Text = "Chispa Launcher";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1040, 660);
        Icon = GlassTheme.LoadIcon();
        DoubleBuffered = true;
        MinimumSize = MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10);
        MaximizeBox = false;

        loginPage.Dock = DockStyle.Fill;
        loginPage.BackColor = Background;
        libraryPage.Dock = DockStyle.Fill;
        libraryPage.BackColor = Background;
        libraryPage.Visible = false;
        Controls.Add(libraryPage);
        Controls.Add(loginPage);
        ConfigureWindowChrome(loginPage);
        ConfigureWindowChrome(libraryPage);

        BuildLoginPage();
        BuildLibraryPage();
        catalogTimer.Tick += async (_, _) =>
        {
            if (libraryPage.Visible && !busy) await RunBusy(() => RefreshCatalogAsync(silent: true));
        };
        var saved = CredentialStore.Load(api.ServerUrl);
        if (saved is not null)
        {
            username.Text = saved.Value.Username;
            password.Text = saved.Value.Password;
            remember.Checked = true;
            SetStatus("已载入保存的登录信息。");
        }
        else SetStatus("输入账号密码后登录。");
        AcceptButton = login;
        UpdateControls();
    }

    private void BuildLoginPage()
    {
        loginPage.Decorations = PaintLoginArt;
        var card = new GlassPanel { Bounds = new Rectangle(558, 48, 434, 564) };
        loginPage.Controls.Add(card);
        AddLabel(card, "YOUR NEXT SPARK", 32, 27, 9, Accent, true, 365);
        AddLabel(card, "欢迎回来", 29, 61, 27, Color.White, true, 365);
        AddLabel(card, "登录 Chispa，打开你的专属程序库。", 32, 114, 10, Muted, false, 370);
        AddLabel(card, "账号", 32, 164, 10, Muted, false, 300);
        ConfigureText(card, username, 32, 193, 370);
        username.PlaceholderText = "输入账号";
        AddLabel(card, "密码", 32, 256, 10, Muted, false, 300);
        ConfigureText(card, password, 32, 285, 370);
        password.PlaceholderText = "输入密码";
        password.UseSystemPasswordChar = true;
        remember.SetBounds(34, 345, 200, 28);
        remember.Text = "记住登录信息";
        remember.ForeColor = Muted;
        card.Controls.Add(remember);
        showPassword.SetBounds(270, 345, 135, 28);
        showPassword.Text = "显示密码";
        showPassword.ForeColor = Muted;
        showPassword.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !showPassword.Checked;
        card.Controls.Add(showPassword);
        ConfigureButton(card, login, "进入程序库   →", 32, 393, 370, true);
        login.Height = 48;
        ConfigureButton(card, testConnection, "测试连接", 32, 452, 370, false);
        loginStatus.SetBounds(32, 508, 370, 45);
        loginStatus.ForeColor = Muted;
        loginStatus.BackColor = Color.Transparent;
        loginStatus.Font = new Font("Microsoft YaHei UI", 9);
        loginStatus.AccessibleName = "登录状态";
        card.Controls.Add(loginStatus);
        login.Click += async (_, _) => await RunBusy(async () =>
        {
            var account = username.Text.Trim();
            var secret = password.Text;
            await api.LoginAsync(account, secret, lifetime.Token);
            var programs = await api.GetProgramsAsync(lifetime.Token);
            string? saveError = null;
            try
            {
                if (remember.Checked) CredentialStore.Save(api.ServerUrl, account, secret);
                else CredentialStore.Delete();
            }
            catch (Exception ex) { saveError = ex.Message; }
            password.Clear();
            welcome.Text = "欢迎，" + account;
            loginPage.Visible = false;
            libraryPage.Visible = true;
            AcceptButton = null;
            LoadProgramCards(programs);
            catalogCount.Text = $"{programs.Count} 个已授权选项";
            catalogTimer.Start();
            if (programs.Count == 0)
                SetStatus("暂无已授权的程序。");
            else
            {
                selected = programs[0];
                MarkSelection();
                await CheckVersionAsync();
            }
            if (saveError is not null) SetStatus("登录成功，但无法保存登录信息：" + saveError);
        });
        testConnection.Click += async (_, _) => await RunBusy(async () =>
        {
            SetStatus("正在测试客户端到服务器的连接…");
            var elapsed = await api.CheckConnectivityAsync(lifetime.Token);
            SetStatus($"连接成功，耗时 {elapsed.TotalMilliseconds:0} 毫秒。");
        });
    }

    private void BuildLibraryPage()
    {
        libraryPage.Decorations = (g, scale) =>
            g.DrawImage(GlassTheme.Logo, 32 * scale, 29 * scale, 44 * scale, 44 * scale);
        AddLabel(libraryPage, "chispa", 88, 23, 24, Color.White, true, 180);
        AddLabel(libraryPage, "让每一次启动，都有新的闪光。", 90, 65, 9, Muted, false, 340);
        welcome.SetBounds(650, 34, 230, 30);
        welcome.ForeColor = Muted;
        welcome.BackColor = Color.Transparent;
        welcome.TextAlign = ContentAlignment.MiddleRight;
        libraryPage.Controls.Add(welcome);
        ConfigureButton(libraryPage, logout, "退出登录", 900, 30, 108, false);
        logout.Click += async (_, _) => await RunBusy(async () =>
        {
            var message = "已退出登录。";
            try { await api.LogoutAsync(lifetime.Token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                message = "已在本机退出登录，服务器暂时无法连接。";
            }
            finally { if (!IsDisposed) ReturnToLogin(message); }
        });
        AddLabel(libraryPage, "程序库", 32, 112, 21, Color.White, true, 230);
        catalogCount.SetBounds(155, 124, 420, 24);
        catalogCount.ForeColor = Muted;
        catalogCount.BackColor = Color.Transparent;
        libraryPage.Controls.Add(catalogCount);
        ConfigureButton(libraryPage, syncCatalog, "同步程序", 880, 110, 128, false);
        syncCatalog.Click += async (_, _) => await RunBusy(() => RefreshCatalogAsync(silent: false));
        programCards.SetBounds(32, 174, 604, 362);
        programCards.AutoScroll = true;
        programCards.WrapContents = true;
        programCards.BackColor = Color.Transparent;
        programCards.Padding = new Padding(0);
        libraryPage.Controls.Add(programCards);
        var detail = new GlassPanel { Bounds = new Rectangle(660, 174, 348, 362) };
        libraryPage.Controls.Add(detail);
        AddLabel(detail, "准备就绪，从这里开始", 24, 21, 9, Accent, true, 296);
        programName.SetBounds(23, 53, 300, 58);
        programName.ForeColor = Color.White;
        programName.BackColor = Color.Transparent;
        programName.Font = new Font("Microsoft YaHei UI", 19, FontStyle.Bold);
        programName.AutoEllipsis = true;
        detail.Controls.Add(programName);
        version.SetBounds(25, 121, 298, 58);
        version.ForeColor = Muted;
        version.BackColor = Color.Transparent;
        version.Font = new Font("Microsoft YaHei UI", 9);
        detail.Controls.Add(version);
        ConfigureButton(detail, refresh, "检查更新", 24, 188, 300, false);
        ConfigureButton(detail, install, "下载 / 更新", 24, 241, 300, true);
        ConfigureButton(detail, launch, "启动程序  ↗", 24, 294, 300, false);
        refresh.Click += async (_, _) => await RunBusy(CheckVersionAsync);
        install.Click += async (_, _) => await RunBusy(async () =>
        {
            if (selected is null || latest is null) return;
            progress.Value = 0;
            SetStatus($"正在下载 {selected.Name} {latest.Version}…");
            var report = new Progress<int>(value => progress.Value = Math.Clamp(value, 0, 100));
            await installer.InstallAsync(api, selected, latest, report, lifetime.Token);
            SetStatus($"{selected.Name} {latest.Version} 已安装。");
        });
        launch.Click += async (_, _) => await RunBusy(async () =>
        {
            if (selected is null) return;
            SetStatus("正在验证授权与程序完整性…");
            await installer.LaunchAsync(api, selected, lifetime.Token);
            SetStatus($"{selected.Name} 已启动。");
        });
        var status = new GlassPanel { Bounds = new Rectangle(32, 560, 976, 70) };
        libraryPage.Controls.Add(status);
        libraryStatus.SetBounds(20, 16, 934, 30);
        libraryStatus.ForeColor = Muted;
        libraryStatus.BackColor = Color.Transparent;
        libraryStatus.Font = new Font("Microsoft YaHei UI", 9);
        libraryStatus.AccessibleName = "下载与启动状态";
        status.Controls.Add(libraryStatus);
        progress.SetBounds(20, 54, 934, 3);
        status.Controls.Add(progress);
    }

    private void ReturnToLogin(string message)
    {
        if (IsDisposed || Disposing) return;
        catalogTimer.Stop();
        api.ClearSession();
        selected = null;
        latest = null;
        LoadProgramCards([]);
        password.Clear();
        showPassword.Checked = false;
        libraryPage.Visible = false;
        loginPage.Visible = true;
        AcceptButton = login;
        SetStatus(message);
        password.Focus();
    }

    private void LoadProgramCards(IReadOnlyList<ProgramInfo> programs)
    {
        foreach (Control card in programCards.Controls.Cast<Control>().ToArray()) card.Dispose();
        programCards.Controls.Clear();
        if (programs.Count == 0)
        {
            programCards.Controls.Add(new Label { Text = "还没有可用程序\n授权后，点击“同步程序”即可查看。", Width = 555, Height = 150,
                ForeColor = Muted, BackColor = Color.Transparent, Padding = new Padding(20), Font = new Font("Microsoft YaHei UI", 12) });
        }
        foreach (var program in programs)
        {
            var card = new ProgramTile(program);
            if (DeviceDpi != 96) card.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
            card.Click += async (_, _) =>
            {
                if (busy) return;
                selected = program;
                latest = null;
                progress.Value = 0;
                MarkSelection();
                await RunBusy(CheckVersionAsync);
            };
            programCards.Controls.Add(card);
        }
    }

    private async Task RefreshCatalogAsync(bool silent)
    {
        var programs = await api.GetProgramsAsync(lifetime.Token);
        var current = programCards.Controls.OfType<ProgramTile>().Select(card => card.Program).ToArray();
        if (current.SequenceEqual(programs))
        {
            if (!silent) SetStatus("程序列表已同步，暂无变化。");
            return;
        }
        var previousId = selected?.Id;
        LoadProgramCards(programs);
        catalogCount.Text = $"{programs.Count} 个已授权选项";
        selected = programs.FirstOrDefault(p => p.Id == previousId) ?? programs.FirstOrDefault();
        latest = null;
        MarkSelection();
        if (selected is not null) await CheckVersionAsync();
        SetStatus($"程序列表已同步，当前有 {programs.Count} 个选项。");
    }

    private void MarkSelection()
    {
        foreach (var card in programCards.Controls.OfType<ProgramTile>())
        {
            card.IsSelected = card.Program.Id == selected?.Id;
            card.Invalidate();
        }
        UpdateControls();
    }

    private async Task CheckVersionAsync()
    {
        if (selected is null) return;
        if (selected.HasRelease == false)
        {
            latest = null;
            SetStatus($"{selected.Name} 尚未发布版本，敬请期待。");
            UpdateControls();
            return;
        }
        SetStatus($"正在查询 {selected.Name} 的版本…");
        latest = await api.GetLatestAsync(selected.Id, lifetime.Token);
        SetStatus(installer.InstalledVersion(selected) == latest.Version
            ? $"{selected.Name} 已是最新版本。"
            : $"{selected.Name} {latest.Version} 可下载。");
        UpdateControls();
    }

    private async Task RunBusy(Func<Task> operation)
    {
        if (busy) return;
        busy = true;
        UpdateControls();
        try { await operation(); }
        catch (SessionExpiredException) { ReturnToLogin("登录已失效，请重新登录。"); }
        catch (HttpRequestException) { SetStatus("无法连接服务器，请检查服务端是否启动，并使用“测试连接”诊断。"); }
        catch (TaskCanceledException) { SetStatus("连接超时，请检查网络或稍后重试。"); }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { busy = false; if (!IsDisposed) UpdateControls(); }
    }

    private void SetStatus(string message)
    {
        if (IsDisposed || Disposing) return;
        if (libraryPage.Visible) libraryStatus.Text = message;
        else loginStatus.Text = message;
    }

    private void UpdateControls()
    {
        var installed = selected is null ? null : installer.InstalledVersion(selected);
        programName.Text = selected?.Name ?? "请选择程序";
        version.Text = selected is null ? "" : selected.HasRelease != false
            ? $"本机版本：{installed ?? "未安装"}\n服务器版本：{latest?.Version ?? "查询中"}"
            : "即将推出\n服务器尚未发布运行包";
        logout.Enabled = !busy;
        showPassword.Enabled = !busy;
        login.Text = busy && loginPage.Visible ? "正在连接…" : "进入程序库   →";
        login.Enabled = !busy;
        testConnection.Enabled = !busy;
        syncCatalog.Enabled = !busy;
        username.Enabled = !busy;
        password.Enabled = !busy;
        remember.Enabled = !busy;
        programCards.Enabled = !busy;
        refresh.Enabled = !busy && selected?.HasRelease != false && selected is not null;
        install.Enabled = !busy && selected?.HasRelease != false && selected is not null && latest is not null;
        install.Text = installed is not null && installed == latest?.Version ? "重新安装 / 修复" : "下载 / 更新";
        ((GlassButton)install).Primary = installed is null || installed != latest?.Version;
        ((GlassButton)launch).Primary = installed is not null;
        install.Invalidate();
        launch.Invalidate();
        launch.Enabled = !busy && selected?.HasRelease != false && selected is not null && installed is not null;
    }

    private static void PaintLoginArt(Graphics g, float scale)
    {
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(GlassTheme.Logo, 44, 34, 40, 40);
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Muted);
        using var brand = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("chispa", brand, white, new RectangleF(95, 32, 240, 44));
        GlassTheme.Glow(g, new Rectangle(20, 90, 470, 360), Color.FromArgb(60, 94, 237, 211));
        using var orbit = new Pen(Color.FromArgb(33, 184, 232, 234));
        g.DrawEllipse(orbit, 100, 136, 290, 236);
        g.DrawEllipse(orbit, 70, 110, 350, 290);
        g.DrawImage(GlassTheme.Mark, 145, 144, 220, 220);
        using var headline = new Font("Microsoft YaHei UI", 36, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("点亮你的下一程。", headline, white, new RectangleF(48, 429, 460, 58));
        using var sub = new Font("Microsoft YaHei UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
        g.DrawString("你的程序，一处就绪。", sub, muted, new RectangleF(50, 491, 420, 32));
        using var small = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("C H I S P A   /   A LITTLE SPARK. A NEW START.", small, muted, new RectangleF(50, 589, 440, 25));
    }

    private static void AddLabel(Control parent, string text, int x, int y, int fontSize,
        Color color, bool bold, int width)
    {
        var font = new Font("Microsoft YaHei UI", fontSize, bold ? FontStyle.Bold : FontStyle.Regular);
        var measured = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        parent.Controls.Add(new Label
        {
            Text = text, Left = x, Top = y, Width = width,
            Height = Math.Max(measured.Height, font.Height),
            ForeColor = color, BackColor = Color.Transparent, Font = font
        });
    }

    private static void ConfigureText(Control parent, TextBox box, int x, int y, int width)
    {
        var shell = new GlassField { Bounds = new Rectangle(x, y, width, 46) };
        box.SetBounds(14, 11, width - 28, 26);
        box.BackColor = GlassTheme.Field;
        box.ForeColor = Color.White;
        box.BorderStyle = BorderStyle.None;
        box.Enter += (_, _) => shell.Highlighted = true;
        box.Leave += (_, _) => shell.Highlighted = false;
        shell.Controls.Add(box);
        parent.Controls.Add(shell);
    }

    private static void ConfigureButton(Control parent, Button button, string text,
        int x, int y, int width, bool primary)
    {
        button.Text = text;
        button.Left = x; button.Top = y;
        button.Width = width; button.Height = 40;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        if (button is GlassButton glass) glass.Primary = primary;
        button.BackColor = GlassTheme.Background;
        button.ForeColor = primary ? Background : Color.White;
        button.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        parent.Controls.Add(button);
    }

    private void ConfigureWindowChrome(GlassCanvas page)
    {
        page.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || e.Y >= 30 * DeviceDpi / 96) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
        };
        var minimize = new ChromeButton(close: false)
        {
            Bounds = new Rectangle(952, 4, 40, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        page.Controls.Add(minimize);
        var close = new ChromeButton(close: true)
        {
            Bounds = new Rectangle(995, 4, 40, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        close.Click += (_, _) => Close();
        page.Controls.Add(close);
        minimize.BringToFront();
        close.BringToFront();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        GlassTheme.ApplyWindowMaterial(Handle);
        RoundWindowCorners();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated) RoundWindowCorners();
    }

    private void RoundWindowCorners()
    {
        if (Width <= 0 || Height <= 0) return;
        // Clip the custom window at all four corners on Windows 10 and 11.
        using var outline = GlassTheme.Rounded(new RectangleF(0, 0, Width, Height),
            Math.Max(16, (int)Math.Round(20f * DeviceDpi / 96f)));
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        lifetime.Cancel();
        lifetime.Dispose();
        catalogTimer.Stop();
        catalogTimer.Dispose();
        Icon?.Dispose();
        api.Dispose();
        base.OnFormClosed(e);
    }
}
