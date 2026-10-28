using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Chispa.Launcher;

internal sealed partial class LauncherForm : Form
{
    private readonly LauncherApi api;
    private readonly Installer installer;
    private readonly GlassCanvas loginPage = new();
    private readonly GlassCanvas libraryPage = new();
    private readonly TextBox username = new();
    private readonly TextBox password = new();
    private readonly GlassCheck remember = new();
    private readonly FlowLayoutPanel programCards = new();
    private readonly Label loginStatus = new GlassLabel();
    private readonly Label libraryStatus = new GlassLabel();
    private readonly Label programName = new GlassLabel();
    private readonly Label version = new GlassLabel();
    private readonly Label welcome = new GlassLabel();
    private readonly Label accountTime = new GlassLabel();
    private readonly Label accountExpiry = new GlassLabel();
    private readonly Label catalogCount = new GlassLabel();
    private readonly GlassCanvas windowSurface = new();
    private readonly GlassProgress progress = new();
    private readonly GlassButton logout = new();
    private readonly GlassCheck showPassword = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly GlassButton login = new();
    private readonly GlassButton registerAccount = new();
    private readonly GlassButton redeemCode = new();
    private readonly GlassButton syncCatalog = new();
    private readonly GlassButton refresh = new();
    private readonly GlassButton install = new();
    private readonly GlassButton launch = new();
    private ProgramInfo? selected;
    private Release? latest;
    private bool busy;
    private bool backgroundSync;
    private bool displayedExpired;
    private int sessionGeneration;
    private readonly System.Windows.Forms.Timer catalogTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer accountTimer = new() { Interval = 1_000 };

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
        SuspendLayout();
        Text = "Chispa Launcher";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(1040, 696);
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
        windowSurface.Name = "windowSurface";
        windowSurface.Dock = DockStyle.Fill;
        Controls.Add(windowSurface);
        var pages = new GlassLayer { Size = new Size(1040, 660), Dock = DockStyle.Fill };
        windowSurface.Controls.Add(pages);
        loginPage.IsContentLayer = libraryPage.IsContentLayer = true;
        loginPage.Size = libraryPage.Size = new Size(1040, 660);
        pages.Controls.Add(libraryPage);
        pages.Controls.Add(loginPage);
        ConfigureWindowChrome();

        BuildLoginPage();
        BuildLibraryPage();
        BuildRegistrationPage(pages);
        accountTimer.Tick += (_, _) =>
        {
            UpdateAccountTime();
            if (displayedExpired != SelectedExpired)
            {
                displayedExpired = SelectedExpired;
                UpdateControls();
            }
        };
        catalogTimer.Tick += async (_, _) =>
        {
            if (libraryPage.Visible && !busy && !backgroundSync) await BackgroundSyncAsync();
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
        ResumeLayout(performLayout: true);
    }

    private void BuildLoginPage()
    {
        loginPage.Decorations = PaintLoginArt;
        var card = new GlassPanel { Bounds = new Rectangle(558, 48, 434, 564) };
        loginPage.Controls.Add(card);
        AddLabel(card, "YOUR NEXT SPARK", 32, 27, 9, Accent, true, 365);
        AddLabel(card, "欢迎回来", 29, 61, 27, Color.White, true, 365);
        AddLabel(card, "登录 Chispa，打开你的专属程序库。", 32, 126, 10, Muted, false, 370);
        AddLabel(card, "账号", 32, 164, 10, Muted, false, 300);
        ConfigureText(card, username, 32, 193, 370);
        username.PlaceholderText = "邮箱或后台创建的账号";
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
        ConfigureButton(card, registerAccount, "注册账户", 32, 452, 370, false);
        loginStatus.SetBounds(32, 508, 370, 45);
        loginStatus.ForeColor = Muted;
        loginStatus.BackColor = Color.Transparent;
        loginStatus.Font = new Font("Microsoft YaHei UI", 9);
        loginStatus.AccessibleName = "登录状态";
        card.Controls.Add(loginStatus);
        // Update notice: the server says which loader build is current; the user decides whether to
        // download it. Nothing is fetched or executed here - a validated https link is all that is
        // ever offered, and only it opens when the text is clicked.
        loginStatus.Cursor = Cursors.Default;
        loginStatus.Click += (_, _) =>
        {
            if (launcherDownload is null) return;
            try
            {
                Process.Start(new ProcessStartInfo(launcherDownload) { UseShellExecute = true });
            }
            catch (Exception ex) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "chispa-launcher-error.log"), DateTimeOffset.Now + Environment.NewLine + ex + Environment.NewLine); } catch { } }
        };
        _ = CheckLauncherUpdateAsync();
        login.Click += async (_, _) => await RunBusy(async () =>
        {
            var account = username.Text.Trim();
            var secret = password.Text;
            await api.LoginAsync(account, secret, lifetime.Token);
            sessionGeneration++;
            var programs = await api.GetProgramsAsync(lifetime.Token);
            string? saveError = null;
            try
            {
                if (remember.Checked) CredentialStore.Save(api.ServerUrl, account, secret);
                else CredentialStore.Delete();
            }
            catch (Exception ex) { saveError = ex.Message; }
            password.Clear();
            welcome.Text = "欢迎，" + (api.Account?.Username ?? account);
            UpdateAccountTime();
            loginPage.Visible = false;
            libraryPage.Visible = true;
            AcceptButton = null;
            LoadProgramCards(programs);
            catalogCount.Text = $"{programs.Count} 个已授权选项";
            catalogTimer.Start();
            accountTimer.Start();
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
        registerAccount.Click += (_, _) => ShowRegistration();
    }

    private string? launcherDownload;

    /// <summary>Compares this build with what the server says is current. A failure is silent: an
    /// update hint must never get in the way of signing in.</summary>
    private async Task CheckLauncherUpdateAsync()
    {
        var current = typeof(LauncherForm).Assembly.GetName().Version;
        var latest = await api.GetLauncherLatestAsync(lifetime.Token);
        if (latest is null || !Version.TryParse(latest.Version, out var serverVersion) || current is null) return;
        if (serverVersion <= current) return;
        launcherDownload = string.IsNullOrEmpty(latest.DownloadUrl) ? null : latest.DownloadUrl;
        if (loginStatus.IsDisposed || lifetime.IsCancellationRequested) return;
        loginStatus.BeginInvoke(() =>
        {
            if (loginStatus.IsDisposed) return;
            var note = string.IsNullOrWhiteSpace(latest.Notes) ? "" : "（" + latest.Notes + "）";
            loginStatus.Text = launcherDownload is null
                ? $"加载器有新版本 {latest.Version}{note}，请联系管理员获取。"
                : $"加载器有新版本 {latest.Version}{note}，点击这里下载。";
            loginStatus.Cursor = launcherDownload is null ? Cursors.Default : Cursors.Hand;
        });
    }

    private void BuildLibraryPage()
    {
        libraryPage.Decorations = (g, scale) =>
            g.DrawImage(GlassTheme.Logo, 32 * scale, 29 * scale, 44 * scale, 44 * scale);
        AddLabel(libraryPage, "chispa", 88, 23, 24, Color.White, true, 180);
        AddLabel(libraryPage, "让每一次启动，都有新的闪光。", 90, 78, 9, Muted, false, 280);
        var accountPanel = new GlassPanel { Name = "accountPanel", Bounds = new Rectangle(400, 18, 458, 100) };
        libraryPage.Controls.Add(accountPanel);
        welcome.SetBounds(16, 9, 426, 26);
        welcome.ForeColor = Muted;
        welcome.BackColor = Color.Transparent;
        welcome.TextAlign = ContentAlignment.MiddleLeft;
        welcome.AutoEllipsis = true;
        accountPanel.Controls.Add(welcome);
        accountTime.SetBounds(16, 37, 426, 26);
        accountTime.ForeColor = Muted;
        accountTime.BackColor = Color.Transparent;
        accountTime.TextAlign = ContentAlignment.TopLeft;
        accountTime.Font = new Font("Microsoft YaHei UI", 10);
        accountTime.AccessibleName = "账户剩余时间";
        accountPanel.Controls.Add(accountTime);
        accountExpiry.SetBounds(16, 65, 426, 26);
        accountExpiry.Font = new Font("Microsoft YaHei UI", 9);
        accountExpiry.ForeColor = Muted;
        accountExpiry.BackColor = Color.Transparent;
        accountExpiry.AccessibleName = "账户到期时间";
        accountPanel.Controls.Add(accountExpiry);
        ConfigureButton(libraryPage, logout, "退出登录", 880, 18, 128, false);
        ConfigureButton(libraryPage, redeemCode, "兑换激活码", 880, 70, 128, false);
        redeemCode.Click += (_, _) => ShowRedemption();
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
        AddLabel(libraryPage, "程序库", 32, 130, 21, Color.White, true, 120);
        catalogCount.SetBounds(164, 143, 420, 26);
        catalogCount.ForeColor = Muted;
        catalogCount.BackColor = Color.Transparent;
        libraryPage.Controls.Add(catalogCount);
        ConfigureButton(libraryPage, syncCatalog, "同步程序", 880, 130, 128, false);
        syncCatalog.Click += async (_, _) => await RunBusy(() => RefreshCatalogAsync(silent: false));
        programCards.SetBounds(32, 190, 604, 350);
        programCards.AutoScroll = true;
        programCards.WrapContents = true;
        programCards.BackColor = Color.Transparent;
        programCards.Padding = new Padding(0);
        libraryPage.Controls.Add(programCards);
        var detail = new GlassPanel { Bounds = new Rectangle(660, 190, 348, 350) };
        libraryPage.Controls.Add(detail);
        AddLabel(detail, "准备就绪，从这里开始", 24, 21, 9, Accent, true, 296);
        programName.SetBounds(23, 50, 300, 66);
        programName.ForeColor = Color.White;
        programName.BackColor = Color.Transparent;
        programName.Font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold);
        programName.AutoEllipsis = true;
        detail.Controls.Add(programName);
        version.SetBounds(25, 122, 298, 46);
        version.ForeColor = Muted;
        version.BackColor = Color.Transparent;
        version.Font = new Font("Microsoft YaHei UI", 9);
        detail.Controls.Add(version);
        ConfigureButton(detail, refresh, "检查更新", 24, 176, 300, false);
        ConfigureButton(detail, install, "下载 / 更新", 24, 229, 300, true);
        ConfigureButton(detail, launch, "启动程序  ↗", 24, 282, 300, false);
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
        sessionGeneration++;
        accountTimer.Stop();
        api.ClearSession();
        selected = null;
        latest = null;
        LoadProgramCards([]);
        password.Clear();
        showPassword.Checked = false;
        registrationPage.Visible = false;
        registrationPassword.Clear();
        registrationCode.Clear();
        libraryPage.Visible = false;
        loginPage.Visible = true;
        AcceptButton = login;
        SetStatus(message);
        password.Focus();
    }

    private void LoadProgramCards(IReadOnlyList<ProgramInfo> programs)
    {
        var scroll = programCards.AutoScrollPosition;
        programCards.SuspendLayout();
        foreach (Control card in programCards.Controls.Cast<Control>().ToArray()) card.Dispose();
        programCards.Controls.Clear();
        if (programs.Count == 0)
        {
            programCards.Controls.Add(new GlassLabel { Text = "还没有可用程序\n授权后，点击“同步程序”即可查看。", Width = 555, Height = 150,
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
        programCards.ResumeLayout();
        programCards.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    private async Task BackgroundSyncAsync()
    {
        backgroundSync = true;
        var generation = sessionGeneration;
        try { await RefreshCatalogAsync(silent: true); }
        catch (SessionExpiredException)
        {
            if (generation == sessionGeneration && libraryPage.Visible && !busy)
                ReturnToLogin("登录已失效，请重新登录。");
        }
        catch (Exception ex)
        {
            // Preserve the current view and last server deadline; the next scheduled sync retries.
            Debug.WriteLine("Background account sync: " + ex.GetType().Name);
        }
        finally { backgroundSync = false; }
    }

    private async Task RefreshCatalogAsync(bool silent)
    {
        var generation = sessionGeneration;
        var wasExpired = api.AccountExpired;
        await api.RefreshAccountAsync(lifetime.Token);
        if (silent && (busy || generation != sessionGeneration || !libraryPage.Visible)) return;
        UpdateAccountTime();
        var programs = await api.GetProgramsAsync(lifetime.Token);
        if (silent && (busy || generation != sessionGeneration || !libraryPage.Visible)) return;
        var current = programCards.Controls.OfType<ProgramTile>().Select(card => card.Program).ToArray();
        if (current.SequenceEqual(programs))
        {
            if (wasExpired && !api.AccountExpired && selected is not null) await CheckVersionAsync(silent);
            UpdateControls();
            if (!silent) SetStatus("程序列表已同步，暂无变化。");
            return;
        }
        var previousId = selected?.Id;
        LoadProgramCards(programs);
        catalogCount.Text = $"{programs.Count} 个已授权选项";
        selected = programs.FirstOrDefault(p => p.Id == previousId) ?? programs.FirstOrDefault();
        latest = null;
        MarkSelection();
        if (selected is not null) await CheckVersionAsync(silent);
        if (!silent) SetStatus($"程序列表已同步，当前有 {programs.Count} 个选项。");
    }

    private void MarkSelection()
    {
        foreach (var card in programCards.Controls.OfType<ProgramTile>())
        {
            var isSelected = card.Program.Id == selected?.Id;
            if (card.IsSelected != isSelected) { card.IsSelected = isSelected; card.Invalidate(); }
        }
        UpdateControls();
        UpdateAccountTime();
    }

    private Task CheckVersionAsync() => CheckVersionAsync(false);

    private async Task CheckVersionAsync(bool silent)
    {
        if (selected is null) return;
        if (SelectedExpired)
        {
            latest = null;
            SetStatus("此程序授权已到期，可兑换包含此程序的激活码续期。");
            UpdateControls();
            return;
        }
        if (selected.HasRelease == false)
        {
            latest = null;
            SetStatus($"{selected.Name} 尚未发布版本，敬请期待。");
            UpdateControls();
            return;
        }
        var program = selected;
        var generation = sessionGeneration;
        if (!silent) SetStatus($"正在查询 {program.Name} 的版本…");
        var release = await api.GetLatestAsync(program.Id, lifetime.Token);
        if (generation != sessionGeneration || selected?.Id != program.Id || (silent && busy)) return;
        latest = release;
        if (!silent) SetStatus(installer.InstalledVersion(selected) == latest.Version
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
        catch (SessionExpiredException)
        {
            // Subscription expiry still permits account inspection; a revoked login does not.
            try
            {
                await api.RefreshAccountAsync(lifetime.Token);
                UpdateAccountTime();
                if (api.AccountExpired) SetStatus("账户授权已到期，续期后点击“同步程序”即可继续使用。");
                else ReturnToLogin("登录已失效，请重新登录。");
            }
            catch { ReturnToLogin("登录已失效，请重新登录。"); }
        }
        catch (HttpRequestException) { SetStatus("无法连接服务器，请检查网络或稍后重试。"); }
        catch (TaskCanceledException) { SetStatus("连接超时，请检查网络或稍后重试。"); }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { busy = false; if (!IsDisposed) UpdateControls(); }
    }

    private void SetStatus(string message)
    {
        if (IsDisposed || Disposing) return;
        if (libraryPage.Visible) libraryStatus.Text = message;
        else if (registrationPage.Visible) registrationStatus.Text = message;
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
        registerAccount.Enabled = !busy;
        redeemCode.Enabled = !busy;
        submitRegistration.Enabled = !busy;
        registrationBack.Enabled = !busy;
        registrationEmail.Enabled = !busy;
        registrationPassword.Enabled = !busy && !redeeming;
        registrationCode.Enabled = !busy;
        syncCatalog.Enabled = !busy;
        username.Enabled = !busy;
        password.Enabled = !busy;
        remember.Enabled = !busy;
        programCards.Enabled = !busy;
        refresh.Enabled = !busy && !SelectedExpired && selected?.HasRelease != false && selected is not null;
        install.Enabled = !busy && !SelectedExpired && selected?.HasRelease != false && selected is not null && latest is not null;
        install.Text = installed is not null && installed == latest?.Version ? "重新安装 / 修复" : "下载 / 更新";
        ((GlassButton)install).Primary = installed is null || installed != latest?.Version;
        ((GlassButton)launch).Primary = installed is not null;
        launch.Enabled = !busy && !SelectedExpired && selected?.HasRelease != false && selected is not null && installed is not null;
    }

    private bool SelectedExpired => selected?.HasEntitlement == true
        ? selected.EntitlementExpiresAt is { } expiry && expiry <= api.ServerNow : api.AccountExpired;

    private void UpdateAccountTime()
    {
        if (api.Account is not { } account) { accountTime.Text = accountExpiry.Text = ""; return; }
        welcome.Text = "欢迎，" + account.Username;
        if (account.ProgramExpiries?.Count == 0)
        {
            accountTime.Text = "尚未开通稳定程序";
            accountExpiry.Text = "可兑换程序激活码开通授权";
            accountTime.ForeColor = Muted;
            return;
        }
        var deadline = selected?.HasEntitlement == true ? selected.EntitlementExpiresAt : account.AccountExpiresAt;
        var label = selected?.HasEntitlement == true ? "此程序" : "账户";
        if (deadline is not { } expiry)
        {
            accountTime.Text = label + "剩余时间：永久";
            accountExpiry.Text = label + "到期时间：永久有效";
            accountTime.ForeColor = Muted;
            return;
        }
        var remaining = expiry - api.ServerNow;
        var countdown = remaining <= TimeSpan.Zero ? "已到期"
            : $"{(long)remaining.TotalDays} 天 {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        accountTime.Text = $"{label}剩余时间：{countdown}";
        accountExpiry.Text = $"{label}到期时间：{expiry.ToLocalTime():yyyy-MM-dd HH:mm:ss}（本地时间）";
        accountTime.ForeColor = remaining <= TimeSpan.Zero ? Color.Salmon : Muted;
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
        var measured = GlassLabel.Measure(text, font, width);
        parent.Controls.Add(new GlassLabel
        {
            Text = text, Left = x, Top = y, Width = width,
            // Leave room for GDI line-height rounding at fractional monitor scales.
            Height = measured.Height + 4,
            ForeColor = color, BackColor = Color.Transparent, Font = font
        });
    }

    private static void ConfigureText(Control parent, TextBox box, int x, int y, int width)
    {
        var shell = new GlassField { Bounds = new Rectangle(x, y, width, 46) };
        box.BackColor = GlassTheme.Field;
        box.ForeColor = Color.White;
        box.BorderStyle = BorderStyle.None;
        // A single-line native edit chooses its height from the font. Center that actual
        // height after layout/DPI changes instead of freezing a 26-pixel text rectangle.
        box.AutoSize = true;
        shell.Layout += (_, _) =>
        {
            var inset = (int)Math.Round(14 * shell.DeviceDpi / 96f);
            var height = box.PreferredHeight;
            box.SetBounds(inset, Math.Max(0, (shell.ClientSize.Height - height) / 2),
                Math.Max(1, shell.ClientSize.Width - 2 * inset), height);
        };
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

    private void ConfigureWindowChrome()
    {
        var titleBar = new GlassLayer { Name = "windowTitleBar", Dock = DockStyle.Top, Height = 36,
            Padding = new Padding(18, 4, 18, 0), BackColor = Background };
        windowSurface.Controls.Add(titleBar);
        titleBar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
        };
        var minimize = new ChromeButton(close: false)
        {
            Name = "windowMinimize", AccessibleName = "最小化", Dock = DockStyle.Right, Width = 44
        };
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        titleBar.Controls.Add(minimize);
        var close = new ChromeButton(close: true)
        {
            Name = "windowClose", AccessibleName = "关闭", Dock = DockStyle.Right, Width = 44
        };
        close.Click += (_, _) => Close();
        titleBar.Controls.Add(close);
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

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
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
        accountTimer.Stop();
        accountTimer.Dispose();
        Icon?.Dispose();
        api.Dispose();
        base.OnFormClosed(e);
    }
}
