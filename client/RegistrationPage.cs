using System.Net.Mail;

namespace Chispa.Launcher;

internal sealed partial class LauncherForm
{
    private readonly GlassCanvas registrationPage = new();
    private readonly TextBox registrationEmail = new();
    private readonly TextBox registrationPassword = new();
    private readonly TextBox registrationCode = new();
    private readonly GlassButton submitRegistration = new();
    private readonly GlassButton registrationBack = new();
    private readonly Label registrationStatus = new GlassLabel();
    private readonly Label registrationTitle = new GlassLabel();
    private readonly Label registrationHint = new GlassLabel();
    private readonly Label registrationEmailCaption = new GlassLabel();
    private readonly Label registrationPasswordCaption = new GlassLabel();
    private readonly Label renewalSummary = new GlassLabel();
    private bool redeeming;

    private void BuildRegistrationPage(Control pages)
    {
        registrationPage.Size = new Size(1040, 660);
        registrationPage.IsContentLayer = true;
        registrationPage.Dock = DockStyle.Fill;
        registrationPage.BackColor = Background;
        registrationPage.Visible = false;
        registrationPage.Decorations = PaintLoginArt;
        pages.Controls.Add(registrationPage);
        var card = new GlassPanel { Bounds = new Rectangle(558, 48, 434, 564) };
        registrationPage.Controls.Add(card);
        registrationTitle.SetBounds(30, 22, 370, 54);
        registrationTitle.Font = new Font("Microsoft YaHei UI", 25, FontStyle.Bold);
        registrationTitle.BackColor = Color.Transparent;
        card.Controls.Add(registrationTitle);
        registrationHint.SetBounds(32, 80, 370, 46);
        registrationHint.Font = new Font("Microsoft YaHei UI", 9);
        registrationHint.ForeColor = Muted;
        registrationHint.BackColor = Color.Transparent;
        card.Controls.Add(registrationHint);
        registrationEmailCaption.SetBounds(32, 133, 370, 24);
        registrationEmailCaption.BackColor = Color.Transparent;
        registrationEmailCaption.ForeColor = Muted;
        card.Controls.Add(registrationEmailCaption);
        ConfigureText(card, registrationEmail, 32, 158, 370);
        registrationEmail.PlaceholderText = "name@example.com";
        registrationEmail.MaxLength = 254;
        registrationPasswordCaption.SetBounds(32, 217, 370, 24);
        registrationPasswordCaption.Text = "密码（至少 12 位）";
        registrationPasswordCaption.BackColor = Color.Transparent;
        registrationPasswordCaption.ForeColor = Muted;
        card.Controls.Add(registrationPasswordCaption);
        ConfigureText(card, registrationPassword, 32, 242, 370);
        registrationPassword.PlaceholderText = "输入 12 至 128 位密码";
        registrationPassword.MaxLength = 128;
        registrationPassword.UseSystemPasswordChar = true;
        renewalSummary.SetBounds(32, 217, 370, 71);
        renewalSummary.BackColor = Color.Transparent;
        renewalSummary.ForeColor = Muted;
        renewalSummary.Font = new Font("Microsoft YaHei UI", 9);
        renewalSummary.Visible = false;
        card.Controls.Add(renewalSummary);
        AddLabel(card, "激活码 / 密钥", 32, 301, 10, Muted, false, 370);
        ConfigureText(card, registrationCode, 32, 332, 370);
        registrationCode.PlaceholderText = "粘贴 CSP- 开头的激活码";
        registrationCode.MaxLength = 80;
        ConfigureButton(card, submitRegistration, "注册账户", 32, 399, 370, true);
        submitRegistration.Height = 48;
        ConfigureButton(card, registrationBack, "返回登录", 32, 458, 370, false);
        registrationStatus.SetBounds(32, 508, 370, 48);
        registrationStatus.ForeColor = Muted;
        registrationStatus.BackColor = Color.Transparent;
        registrationStatus.Font = new Font("Microsoft YaHei UI", 9);
        card.Controls.Add(registrationStatus);
        registrationBack.Click += (_, _) => LeaveRegistration();
        submitRegistration.Click += async (_, _) => await RunBusy(async () =>
        {
            if (redeeming)
            {
                await api.RedeemAsync(registrationCode.Text, lifetime.Token);
                LeaveRegistration();
                UpdateAccountTime();
                await RefreshCatalogAsync(silent: false);
                SetStatus("激活码兑换成功，账户到期时间已更新。");
                return;
            }
            var email = registrationEmail.Text.Trim();
            if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !email.Contains('@'))
                throw new InvalidOperationException("请输入有效的邮箱地址。");
            if (registrationPassword.Text.Length < 12)
                throw new InvalidOperationException("密码至少需要 12 个字符。");
            await api.RegisterAsync(email, registrationPassword.Text, registrationCode.Text, lifetime.Token);
            username.Text = email.ToLowerInvariant();
            password.Clear();
            LeaveRegistration();
            SetStatus("注册成功，授权已生效。请使用邮箱和密码登录。");
        });
    }

    private void ShowRegistration()
    {
        redeeming = false;
        registrationTitle.Text = "注册账户";
        registrationHint.Text = "使用邮箱作为登录账号，填写激活码开通授权。\n已有账户？登录后可兑换激活码续费。";
        registrationEmailCaption.Text = "邮箱";
        registrationPasswordCaption.Visible = registrationPassword.Parent!.Visible = true;
        renewalSummary.Visible = false;
        registrationEmail.ReadOnly = false;
        registrationEmail.Clear();
        registrationPassword.Clear();
        registrationPassword.PlaceholderText = "输入 12 至 128 位密码";
        registrationCode.Clear();
        registrationStatus.Text = "激活码仅可使用一次，注册成功后开始计时。";
        submitRegistration.Text = "注册账户";
        registrationBack.Text = "返回登录";
        loginPage.Visible = libraryPage.Visible = false;
        registrationPage.Visible = true;
        UpdateControls();
        AcceptButton = submitRegistration;
        registrationEmail.Focus();
    }

    private void ShowRedemption()
    {
        ShowRegistration();
        redeeming = true;
        registrationTitle.Text = "兑换激活码";
        registrationHint.Text = "单程序、多程序和全部程序码可混合兑换，时长分别累加。\nBeta 通行证仅解锁已授权稳定程序对应的 Beta。";
        registrationEmail.Text = api.Account?.Username;
        registrationEmail.ReadOnly = true;
        registrationEmailCaption.Text = "账户";
        registrationPasswordCaption.Visible = registrationPassword.Parent!.Visible = false;
        renewalSummary.Text = api.Account?.ProgramExpiries?.Count == 0 ? "尚未开通稳定程序\n可兑换单程序、多程序或全部程序激活码。" : api.Account?.AccountExpiresAt is { } expiry
            ? $"账户最晚到期时间\n{expiry.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n各程序时间请在程序库中选中查看。"
            : "账户含永久授权\n仍可兑换其他程序或 Beta 通行证。";
        renewalSummary.Visible = true;
        registrationPassword.Enabled = false;
        registrationPassword.PlaceholderText = "已登录，续费无需输入密码";
        registrationStatus.Text = "已有时长会保留；目标均已永久且无新增权限时不消耗激活码。";
        submitRegistration.Text = "确认兑换";
        registrationBack.Text = "返回程序库";
        registrationCode.Focus();
    }

    private void LeaveRegistration()
    {
        registrationPage.Visible = false;
        registrationPassword.Clear();
        registrationCode.Clear();
        if (redeeming)
        {
            libraryPage.Visible = true;
            AcceptButton = null;
        }
        else
        {
            loginPage.Visible = true;
            AcceptButton = login;
        }
    }
}
