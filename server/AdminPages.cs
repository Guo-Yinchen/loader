using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;

static class AdminPages
{
    private static readonly ConcurrentDictionary<string, LoginFailures> FailedLogins = new();
    private static readonly ConcurrentDictionary<string, UploadSession> Uploads = new();
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Local;
    private const long MaxPackageBytes = 1024L * 1024 * 1024;
    private const int UploadChunkBytes = 4 * 1024 * 1024;

    private sealed class UploadSession(string owner, string programId, string version, long size, string path)
    {
        public string Owner { get; } = owner;
        public string ProgramId { get; } = programId;
        public string Version { get; } = version;
        public long Size { get; } = size;
        public string Path { get; } = path;
        public long Received { get; set; }
        public string State { get; set; } = "uploading";
        public string? Error { get; set; }
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed record UploadStart(string ProgramId, string Version, long Size);

    public static void MapAdminPages(this WebApplication app, Store store, ServerMetrics metrics, Func<int> activeSessions)
    {
        app.MapAdminAssets();
        app.MapGet("/admin/login", (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/admin");
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            var message = store.HasAdmin() ? "请使用管理员账号登录。" :
                "尚未创建管理员。先在本机终端运行：./run-local.ps1 admin-create <用户名>";
            return Html(Page("管理员登录", $"""
                <h2>管理后台登录</h2>
                <p class="hint">{Encode(message)}</p>
                {(store.HasAdmin() ? $"""
                <form method="post" action="/admin/login">
                  {Csrf(token)}
                  <label>管理员账号<input name="username" required maxlength="32" autocomplete="username"></label>
                  <label>密码<input name="password" type="password" required autocomplete="current-password"></label>
                  <button>登录</button>
                </form>
                """ : "")}
                """));
        });

        app.MapPost("/admin/login", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            var form = await context.Request.ReadFormAsync();
            var username = form["username"].ToString();
            var password = form["password"].ToString();
            var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now = DateTimeOffset.UtcNow;
            if (FailedLogins.TryGetValue(key, out var failures) && failures.Count >= 5 && failures.Until > now)
                return Html(Page("请稍后重试", "<h1>登录暂时受限</h1><p>失败次数过多，请 15 分钟后再试。</p><a href='/admin/login'>返回登录</a>"), 429);
            if (!store.VerifyAdmin(username, password))
            {
                FailedLogins.AddOrUpdate(key, _ => new LoginFailures(1, now.AddMinutes(15)),
                    (_, old) => old.Until <= now ? new LoginFailures(1, now.AddMinutes(15)) : old with { Count = old.Count + 1 });
                return Html(Page("登录失败", "<h1>登录失败</h1><p>账号或密码错误。</p><a href='/admin/login'>返回登录</a>"), 401);
            }
            FailedLogins.TryRemove(key, out _);
            var claims = new[] { new Claim(ClaimTypes.Name, username), new Claim("admin-hash", store.AdminHash(username)!) };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Redirect("/admin");
        });

        app.MapGet("/admin", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var token = antiforgery.GetAndStoreTokens(context).RequestToken!;
            var users = store.ListUsers();
            var programs = store.ListAllPrograms();
            var stats = metrics.Snapshot(activeSessions());
            var body = new StringBuilder();
            body.Append("""
                <a class='skip-link' href='#main'>跳转到主要内容</a>
                <aside class='sidebar'>
                  <a class='brand' href='/admin#overview'><img src='/admin-assets/chispa.svg' alt='Chispa 闪光标识'><div><strong>chispa</strong><small>CONTROL CENTER</small></div></a>
                  <p class='nav-caption'>工作空间 / WORKSPACE</p>
                  <nav aria-label='管理导航'>
                    <a href='#overview'><span class='nav-icon' aria-hidden='true'>▦</span>服务器概览</a>
                    <a href='#accounts'><span class='nav-icon' aria-hidden='true'>◎</span>账号管理</a>
                    <a href='#programs'><span class='nav-icon' aria-hidden='true'>◇</span>程序管理</a>
                    <a href='#release'><span class='nav-icon' aria-hidden='true'>↑</span>版本发布</a>
                    <a href='#connectivity'><span class='nav-icon' aria-hidden='true'>⌁</span>连接诊断</a>
                  </nav>
                  <div class='sidebar-footer'><p><span class='server-light'></span>Chispa 分发服务</p><form method='post' action='/admin/logout'>
                """).Append(Csrf(token)).Append("""
                    <button class='secondary'>退出登录</button></form></div>
                </aside>
                <main class='workspace' id='main'>
                  <header class='page-header'><div><p class='eyebrow'>CHISPA / CONTROL CENTER</p><h1 id='pageTitle'>服务器概览</h1><p class='sub' id='pageSubtitle'>掌握运行状态，让每一次分发都有条不紊。</p></div><span class='session-pill'>管理员 ·
                """).Append(Encode(context.User.Identity?.Name)).Append("</span></header><noscript><p>JavaScript 未启用，所有管理分区将依次显示。</p></noscript>");
            var notice = context.Request.Query["notice"].ToString();
            var noticeText = notice switch
            {
                "created" => "账号已创建。",
                "updated" => "已保存。",
                "password" => "密码已更新，该账号的旧登录会话已失效。",
                "deleted" => "账号已删除。",
                "program" => "程序信息已保存。",
                "removed" => "程序及其发布包已删除，相关账号的授权已同步清理。",
                "published" => "版本已发布。",
                _ => null
            };
            if (noticeText is not null) body.Append("<p class='notice'>").Append(Encode(noticeText)).Append("</p>");
            var error = context.Request.Query["error"].ToString();
            if (error.Length > 0) body.Append("<p class='error'>").Append(Encode(error)).Append("</p>");
            body.Append("<section id='overview' data-view='overview' class='card'><div class='sectionhead'><h2>实时运行状态</h2><span class='online'>● 运行中</span></div>")
                .Append("<p class='hint'>自 ").Append(Encode(stats.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")))
                .Append(" 启动 · ").Append(Encode(context.Request.Scheme + "://" + context.Request.Host))
                .Append(" · 统计随服务重启清零</p><div class='statgrid'>");
            Metric(body, "总请求", stats.Requests, "requests");
            Metric(body, "最近 1 小时", stats.LastHour.Sum(), "lastHour");
            Metric(body, "API 请求", stats.ApiRequests, "apiRequests");
            Metric(body, "失败响应", stats.FailedRequests, "failedRequests");
            Metric(body, "当前会话", stats.ActiveSessions, "activeSessions");
            Metric(body, "下载请求", stats.Downloads, "downloads");
            body.Append("</div><div class='minirow'><span>运行 <b id='uptimeMinutes'>")
                .Append(stats.UptimeSeconds / 60).Append(" 分钟</b></span><span>客户端登录成功 <b id='loginSuccess'>")
                .Append(stats.LoginSuccess).Append("</b></span><span>客户端登录失败 <b id='loginFailure'>")
                .Append(stats.LoginFailure).Append("</b></span><span>进程内存 <b id='memory'>")
                .Append((stats.MemoryBytes / 1024 / 1024).ToString(CultureInfo.InvariantCulture))
                .Append(" MB</b></span><span>账号 ").Append(users.Count).Append("</span><span>登记程序 ")
                .Append(programs.Count).Append("</span></div><div id='requestChart' class='chart' aria-label='最近 60 分钟请求量'>");
            Chart(body, stats.LastHour);
            body.Append("</div><div class='chart-caption'><span>60 分钟前</span><span>请求趋势 / 每分钟</span><span>现在</span></div><p class='hint' id='metricsStatus' aria-live='polite'>每 15 秒自动更新 · 统计随服务重启清零</p></section>");
            body.Append("<section id='connectivity' data-view='connectivity' class='card'><div class='sectionhead'><h2>客户端连接诊断</h2><span class='online'>实时探测</span></div>")
                .Append("<p class='hint'>让外网电脑打开客户端并点击“测试连接”。成功后这里会出现它的请求时间和来源地址，确认该客户端可以到达服务器。默认的 127.0.0.1 地址仅供本机测试；公网使用前需部署 HTTPS 地址并重新发布客户端。</p>")
                .Append("<div class='minirow'><span>探测次数 <b id='clientProbes'>").Append(stats.ClientProbes)
                .Append("</b></span><span>最近探测 <b id='lastClientProbeAt'>")
                .Append(stats.LastClientProbeAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无")
                .Append("</b></span><span>来源 <b id='lastClientProbeAddress'>")
                .Append(Encode(stats.LastClientProbeAddress ?? "尚无"))
                .Append("</b></span></div><p class='hint'>本机浏览器测试只验证当前浏览器到服务器的连接；外网连通性请在外网客户端测试。</p>")
                .Append("<button id='browserProbe' type='button' class='secondary'>测试当前浏览器连接</button> <span id='browserProbeResult' class='hint' aria-live='polite'></span></section>");
            body.Append("""
                <section id='accounts' data-view='accounts' class='card'><details class='create-panel'><summary>创建账号<span>设置账号、期限与程序权限</span></summary>
                <form method='post' action='/admin/users'>
                """).Append(Csrf(token)).Append("""
                <div class='fields'>
                  <label>账号<input name='username' required minlength='3' maxlength='32' pattern='[A-Za-z0-9_.-]+' autocomplete='off'></label>
                  <label>初始密码<input name='password' type='password' required minlength='12' autocomplete='new-password'></label>
                  <label>到期时间<input name='expiry' type='datetime-local' step='60'></label>
                </div>
                <label>账号分类<select name='accessMode'>
                  <option value='all'>全部稳定程序</option>
                  <option value='single'>单程序</option><option value='multiple'>多程序</option>
                  <option value='none'>未授权任何程序</option>
                </select></label>
                <div class='choices'><span>指定程序（单程序或多程序时选择）</span>
                """).Append(ProgramChoices(programs, [])).Append("""
                </div><label class='check'><input type='checkbox' name='betaAccess' value='1'>允许访问 Beta 程序</label>
                <p class='hint'>Beta 权限可与任一账号分类并行。全部程序默认只包含稳定程序；到期时间按服务器本地时间填写，精确到分钟，留空表示永久。</p>
                <button>创建账号</button></form></details></section>
                <section data-view='accounts' class='card'><div class='sectionhead'><h2>现有账号</h2><input class='list-search' type='search' aria-label='搜索账号' placeholder='搜索账号或权限…' data-filter='.user' data-empty='noAccounts'></div>
                """).Append(ZoneHint()).Append("""
                <p class='empty-state' id='noAccounts' hidden>没有匹配的账号。</p>
                """);
            if (users.Count == 0) body.Append("<p class='empty-state'>还没有账号，点击上方“创建账号”开始。</p>");
            else
            {
                body.Append("<div class='users'>");
                foreach (var user in users)
                {
                    var path = Uri.EscapeDataString(user.Username);
                    var state = user.ExpiresAt is null ? "永久"
                        : user.ExpiresAt <= DateTimeOffset.UtcNow ? "已到期 " + LocalText(user.ExpiresAt.Value)
                        : "有效期至 " + LocalText(user.ExpiresAt.Value);
                    body.Append("<details class='user' data-search='").Append(Encode(user.Username + " " + AccessLabel(user, programs))).Append("'><summary class='userhead'><strong>").Append(Encode(user.Username))
                        .Append("</strong><span>").Append(Encode(state)).Append("</span><span class='badge'>")
                        .Append(Encode(AccessLabel(user, programs))).Append("</span><span class='edit-affordance'>管理账号 ⌄</span></summary><div class='user-editor'>")
                        .Append("<form class='expiryform' method='post' action='/admin/users/").Append(path).Append("/expiry'>")
                        .Append(Csrf(token))
                        .Append("<p class='formhead'>到期时间</p>")
                        .Append("<label>到期时间（本地时间，精确到分钟）<input name='expiry' type='datetime-local' step='60' value='")
                        .Append(Encode(InputText(user.ExpiresAt))).Append("'></label>")
                        .Append(ExpiryHint(user))
                        .Append("<button class='secondary'>保存到期时间</button></form>")
                        .Append("<form class='accessform' method='post' action='/admin/users/").Append(path).Append("/access'>")
                        .Append(Csrf(token)).Append("<p class='formhead'>程序权限</p><label>账号分类<select name='accessMode'>")
                        .Append(ModeOptions(user.AccessMode)).Append("</select></label><div class='choices'><span>指定程序</span>")
                        .Append(ProgramChoices(programs, user.ProgramIds))
                        .Append("</div><label class='check'><input type='checkbox' name='betaAccess' value='1'")
                        .Append(user.BetaAccess ? " checked" : "")
                        .Append(">允许 Beta</label><button class='secondary'>保存权限</button></form>")
                        .Append("<form class='passwordview' method='post' action='/admin/users/").Append(path)
                        .Append("/password/view'>").Append(Csrf(token))
                        .Append("<p class='formhead'>查看密码</p><p class='hint'>")
                        .Append(user.PasswordAvailable
                            ? "仅管理员点击后显示；服务器加密保存此密码，请妥善保管后台登录权限与密钥。"
                            : "此账号在启用密码保存前创建，旧密码只有哈希，无法还原；重置后可查看新密码。")
                        .Append("</p><button class='secondary'")
                        .Append(user.PasswordAvailable ? "" : " disabled")
                        .Append(">显示用户密码</button></form>")
                        .Append("<form class='passwordform' method='post' action='/admin/users/").Append(path)
                        .Append("/password' data-confirm='确认修改账号 “").Append(Encode(user.Username))
                        .Append("” 的密码？保存后该账号当前的登录会话会立即失效，需要用新密码重新登录。'>")
                        .Append(Csrf(token)).Append("<p class='formhead'>重置密码</p>")
                        .Append("<p class='hint'>用户忘记密码时，由管理员在这里直接重设。先勾选确认，再填写两次新密码。</p>")
                        .Append("<label class='check'><input type='checkbox' name='enableReset' value='1' data-reset-toggle>我确认要修改该账号的密码</label>")
                        .Append("<fieldset class='pwfields' data-reset-fields disabled>")
                        .Append("<label>新密码（至少 12 位）<input name='password' type='password' minlength='12' maxlength='128' required autocomplete='new-password'></label>")
                        .Append("<label>再次输入新密码<input name='passwordConfirm' type='password' minlength='12' maxlength='128' required autocomplete='new-password'></label>")
                        .Append("</fieldset><button class='danger' data-reset-submit disabled>保存新密码</button></form>")
                        .Append("<form class='deleteform' method='post' action='/admin/users/").Append(path)
                        .Append("/delete' data-confirm='确定删除该账号？删除后其登录会话立即失效。'>")
                        .Append(Csrf(token)).Append("<button class='danger'>删除账号</button></form></div></details>");
                }
                body.Append("</div>");
            }
            body.Append("</section>");
            body.Append("""
                <section id='programs' data-view='programs' class='card'><div class='sectionhead'><h2>程序与选项</h2><input class='list-search' type='search' aria-label='搜索程序' placeholder='搜索程序…' data-filter='.program' data-empty='noPrograms'></div>
                <p class='hint'>未发布版本的程序是占位项，可先用于测试账号权限；客户端会显示“即将推出”，下载与启动不可用。删除程序会同时移除它的全部发布包，并从账号授权里剔除它。</p>
                <details class='create-panel'><summary>登记程序<span>添加新的程序或 Beta 选项</span></summary>
                <form method='post' action='/admin/programs'>
                """).Append(Csrf(token)).Append("""
                <div class='fields'>
                  <label>程序 ID<input name='id' required minlength='2' maxlength='32' pattern='[a-z0-9][a-z0-9-]+' placeholder='tool-b'></label>
                  <label>显示名称<input name='name' required maxlength='80' placeholder='辅助工具'></label>
                  <label>入口 EXE 文件名<input name='executable' required maxlength='120' placeholder='Tool.exe'></label>
                  <label>文字图标<input name='iconText' maxlength='16' placeholder='🎯'></label>
                </div>
                <label class='check'><input type='checkbox' name='isBeta' value='1'>标记为 Beta 程序</label>
                <p class='hint'>Beta 选项需关联一个稳定程序，授权该稳定程序并开启 Beta 后即可访问对应选项。</p>
                """).Append("<label>所属稳定程序<select name='parentProgramId'>")
                .Append(ParentOptions(programs, null, null)).Append("</select></label>").Append("""
                <button>登记程序</button></form></details><div class='quiet-divider'></div><p class='empty-state' id='noPrograms' hidden>没有匹配的程序。</p>
                """);
            if (programs.Count == 0) body.Append("<p>还没有登记程序。</p>");
            foreach (var program in programs)
            {
                var release = store.Latest(program.Id);
                var icon = program.Icon ?? "◆";
                body.Append("<details class='program' data-search='").Append(Encode(program.Name + " " + program.Id)).Append("'><summary class='programtitle'>").Append(IconHtml(icon))
                    .Append("<div class='program-meta'><strong>").Append(Encode(program.Name)).Append("</strong><span>")
                    .Append(Encode(program.Id)).Append(" · ").Append(Encode(program.Executable)).Append(" · ")
                    .Append(release is null ? "占位，未发布" : "最新版本 " + Encode(release.Version))
                    .Append(program.IsBeta ? " · Beta" : "").Append("</span></div><span class='edit-affordance'>编辑选项 ⌄</span></summary>")
                    .Append("<form method='post' enctype='multipart/form-data' action='/admin/programs/")
                    .Append(Uri.EscapeDataString(program.Id)).Append("/update'>").Append(Csrf(token))
                    .Append("<div class='fields'><label>选项名称<input name='name' required maxlength='80' value='")
                    .Append(Encode(program.Name)).Append("'></label><label>文字图标<input name='iconText' maxlength='16' value='")
                    .Append(icon.StartsWith("data:image/png;base64,", StringComparison.Ordinal) ? "" : Encode(program.Icon))
                    .Append("' placeholder='🎯'></label><label>上传图片并裁剪<input type='file' name='iconFile' data-icon-picker accept='image/png'><img class='icon-upload-preview' alt='裁剪后的图标预览' hidden><span class='icon-result' aria-live='polite'></span></label></div>")
                    .Append("<label>所属稳定程序<select name='parentProgramId'>")
                    .Append(ParentOptions(programs, program.ParentProgramId, program.Id))
                    .Append("</select></label>")
                    .Append("<div class='inlinechecks'><label class='check'><input type='checkbox' name='isBeta' value='1'")
                    .Append(program.IsBeta ? " checked" : "")
                    .Append(">Beta 程序</label><label class='check'><input type='checkbox' name='removeIcon' value='1'>清除图标</label></div>")
                    .Append("<p class='hint'>支持 PNG、JPG、WebP、GIF、BMP、AVIF。选择图片后预览并裁剪，保存时自动转成适合加载器的 PNG 图标。原图不超过 20 MB。</p><button class='secondary'>保存选项</button></form>")
                    .Append("<form class='deleteform' method='post' action='/admin/programs/").Append(Uri.EscapeDataString(program.Id))
                    .Append("/delete' data-confirm='确定删除程序 “").Append(Encode(program.Name)).Append("”（").Append(Encode(program.Id))
                    .Append("）？已发布的全部版本会一并删除，引用它的账号授权会被移除，此操作不可撤销。'>")
                    .Append(Csrf(token)).Append("<button class='danger'>删除程序</button></form></details>");
            }
            body.Append("""
                </section><section id='release' data-view='release' class='card'><h2>发布版本</h2>
                <p class='hint'>选择程序、填写新版本号，并上传包含入口 EXE 和全部运行依赖的 ZIP。入口 EXE 放在 ZIP 顶层；如果整个包只套了一个外层文件夹（例如直接压缩了一个程序文件夹），服务器会自动去掉这一层；__MACOSX、Thumbs.db、desktop.ini 这类系统文件会被忽略。最大 1 GB。</p>
                <form id='releaseUpload' method='post' action='/admin/releases' enctype='multipart/form-data'>
                """).Append(Csrf(token)).Append("<div class='fields'><label>程序<select name='programId' required>");
            foreach (var program in programs)
                body.Append("<option value='").Append(Encode(program.Id)).Append("'>")
                    .Append(Encode(program.Name)).Append(" (").Append(Encode(program.Id)).Append(")</option>");
            body.Append("""
                </select></label><label>版本号<input name='version' required pattern='[0-9]+\.[0-9]+\.[0-9]+' placeholder='1.0.0'></label>
                <label>运行包 ZIP<input name='package' type='file' accept='.zip,application/zip' required></label></div>
                <p class='upload-summary' id='uploadSummary' aria-live='polite'>选择包含入口 EXE 和运行依赖的 ZIP 文件。</p><button>上传并发布</button></form></section></main>
                <dialog id='iconCropDialog' class='icon-crop-dialog' aria-labelledby='iconCropTitle'>
                  <div class='icon-crop-head'><div><p class='eyebrow'>ICON EDITOR</p><h2 id='iconCropTitle'>裁剪图标</h2></div><button type='button' class='secondary' id='iconCropCancel' aria-label='取消裁剪'>取消</button></div>
                  <p class='hint'>拖动图片调整位置，滑动下方控件缩放。右侧是加载器中的圆角预览。</p>
                  <div class='icon-crop-layout'><canvas id='iconCropCanvas' width='256' height='256' aria-label='拖动图片调整裁剪区域'></canvas><div class='icon-crop-side'><canvas id='iconCropPreview' width='96' height='96' aria-label='圆角图标预览'></canvas><span>加载器预览</span></div></div>
                  <label>缩放<input id='iconCropZoom' type='range' min='1' max='3' value='1' step='0.01'></label>
                  <p class='hint' id='iconCropStatus' aria-live='polite'>确认后自动转成 PNG。</p>
                  <button type='button' id='iconCropConfirm'>使用这个裁剪</button>
                </dialog>
                """);
            return Html(Page("概览", body.ToString()));
        }).RequireAuthorization();

        app.MapGet("/admin/metrics", () => Results.Ok(metrics.Snapshot(activeSessions())))
            .RequireAuthorization();

        app.MapPost("/admin/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/admin/login");
        }).RequireAuthorization();

        app.MapPost("/admin/users", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            var form = await context.Request.ReadFormAsync();
            try
            {
                var expiry = ParseExpiry(ExpiryField(form));
                if (expiry is not null && expiry <= DateTimeOffset.UtcNow)
                    throw new ArgumentException("到期时间必须晚于当前时间。");
                store.AddUser(form["username"].ToString(), form["password"].ToString(), expiry,
                    form["accessMode"].ToString() is "" ? "all" : form["accessMode"].ToString(),
                    form["programIds"].Where(id => id is not null).Select(id => id!).ToArray(),
                    form.ContainsKey("betaAccess"));
                return Results.Redirect("/admin?notice=created");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        app.MapPost("/admin/users/{username}/expiry", async (string username, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            var form = await context.Request.ReadFormAsync();
            try
            {
                store.SetUserExpiry(username, ParseExpiry(ExpiryField(form)));
                return Results.Redirect("/admin?notice=updated");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        app.MapPost("/admin/users/{username}/access", async (string username, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            var form = await context.Request.ReadFormAsync();
            try
            {
                store.SetUserAccess(username, form["accessMode"].ToString(),
                    form["programIds"].Where(id => id is not null).Select(id => id!).ToArray(),
                    form.ContainsKey("betaAccess"));
                return Results.Redirect("/admin?notice=updated");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        app.MapPost("/admin/users/{username}/delete", async (string username, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                store.DeleteUser(username);
                return Results.Redirect("/admin?notice=deleted");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        // Second step of the reset flow: the checkbox must be ticked before the server
        // touches the hash, so a mis-click cannot lock a user out of their account.
        app.MapPost("/admin/users/{username}/password/view", async (string username, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                var password = store.ViewUserPassword(username);
                var content = password is null
                    ? "<p class='hint'>旧密码无法还原。请返回账号管理并重置密码。</p>"
                    : "<label>" + Encode(username) + " 的当前密码<input type='text' readonly autocomplete='off' value='" + Encode(password) + "'></label>";
                return Html(Page("查看用户密码", "<h2>查看用户密码</h2>" + content +
                    "<p class='hint'>关闭此页面后，密码不会留在账号列表中。</p><a class='button' href='/admin#accounts'>返回账号管理</a>"));
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Error("无法解密密码，请检查服务器密钥目录是否完整。");
            }
        }).RequireAuthorization();

        app.MapPost("/admin/users/{username}/password", async (string username, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            var form = await context.Request.ReadFormAsync();
            try
            {
                if (!form.ContainsKey("enableReset"))
                    throw new ArgumentException("请先勾选“我确认要修改该账号的密码”。");
                var password = form["password"].ToString();
                if (password.Length < 12) throw new ArgumentException("新密码至少 12 个字符。");
                if (password != form["passwordConfirm"].ToString())
                    throw new ArgumentException("两次输入的新密码不一致。");
                store.SetUserPassword(username, password);
                return Results.Redirect("/admin?notice=password");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        app.MapPost("/admin/programs", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            var form = await context.Request.ReadFormAsync();
            try
            {
                store.AddProgram(form["id"].ToString(), form["name"].ToString(), form["executable"].ToString(),
                    form.ContainsKey("isBeta"), EmptyToNull(form["iconText"].ToString()),
                    EmptyToNull(form["parentProgramId"].ToString()));
                return Results.Redirect("/admin?notice=program");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        app.MapPost("/admin/programs/{id}/update", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = 512 * 1024;
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                var form = await context.Request.ReadFormAsync();
                var current = store.FindProgram(id) ?? throw new ArgumentException("程序不存在。");
                var icon = current.Icon;
                var text = EmptyToNull(form["iconText"].ToString());
                if (text is not null) icon = text;
                var iconFile = form.Files.GetFile("iconFile");
                if (iconFile is not null && iconFile.Length > 0) icon = await ReadPngIcon(iconFile);
                if (form.ContainsKey("removeIcon")) icon = null;
                store.UpdateProgram(id, form["name"].ToString(), icon, form.ContainsKey("isBeta"),
                    EmptyToNull(form["parentProgramId"].ToString()));
                return Results.Redirect("/admin?notice=program");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (InvalidDataException ex) { return Error(ex.Message); }
        }).RequireAuthorization();

        // Small requests keep each upload connection short. The final ZIP check runs
        // after the last chunk, while the admin page polls for the publish result.
        app.MapPost("/admin/uploads", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest(new { error = "表单已过期，请刷新页面。" });
            UploadStart? input;
            try { input = await context.Request.ReadFromJsonAsync<UploadStart>(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException)
            { return Results.BadRequest(new { error = "上传参数无效。" }); }
            if (input is null || input.Size is <= 0 or > MaxPackageBytes ||
                !System.Text.RegularExpressions.Regex.IsMatch(input.Version ?? "", "^[0-9]+\\.[0-9]+\\.[0-9]+$") ||
                store.FindProgram(input.ProgramId ?? "") is null)
                return Results.BadRequest(new { error = "请选择已登记的程序、有效版本号和不超过 1 GB 的 ZIP。" });
            Directory.CreateDirectory(store.UploadDirectory);
            PruneUploads(store.UploadDirectory);
            var id = Guid.NewGuid().ToString("N");
            var path = Path.Combine(store.UploadDirectory, id + ".zip");
            await using (File.Create(path)) { }
            var session = new UploadSession(context.User.Identity!.Name!, input.ProgramId!, input.Version!, input.Size, path);
            Uploads[id] = session;
            return Results.Ok(new { id, chunkSize = UploadChunkBytes });
        }).RequireAuthorization();

        app.MapPut("/admin/uploads/{id}", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = UploadChunkBytes + 1024;
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest(new { error = "表单已过期，请刷新页面。" });
            if (!TryUpload(id, context, out var session)) return Results.NotFound();
            var length = context.Request.ContentLength;
            if (length is null or <= 0 or > UploadChunkBytes ||
                !long.TryParse(context.Request.Headers["X-Upload-Offset"], out var offset))
                return Results.BadRequest(new { error = "上传分块无效。" });
            await session.Gate.WaitAsync(context.RequestAborted);
            try
            {
                if (session.State != "uploading") return Results.Conflict(new { error = "上传已结束。" });
                if (offset < session.Received) return Results.Ok(new { received = session.Received });
                if (offset != session.Received || offset + length > session.Size)
                    return Results.Conflict(new { error = "上传位置不匹配。" });
                await using var output = new FileStream(session.Path, FileMode.Open, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous);
                output.Position = offset;
                try
                {
                    await context.Request.Body.CopyToAsync(output, context.RequestAborted);
                    if (output.Position != offset + length) throw new InvalidDataException("上传分块不完整。");
                    await output.FlushAsync(context.RequestAborted);
                }
                catch
                {
                    output.SetLength(offset);
                    throw;
                }
                session.Received = offset + length.Value;
                session.Updated = DateTimeOffset.UtcNow;
                return Results.Ok(new { received = session.Received });
            }
            finally { session.Gate.Release(); }
        }).RequireAuthorization();

        app.MapPost("/admin/uploads/{id}/complete", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest(new { error = "表单已过期，请刷新页面。" });
            if (!TryUpload(id, context, out var session)) return Results.NotFound();
            await session.Gate.WaitAsync(context.RequestAborted);
            try
            {
                if (session.State is "processing" or "published")
                    return Results.Accepted($"/admin/uploads/{id}");
                if (session.State != "uploading" || session.Received != session.Size)
                    return Results.Conflict(new { error = "ZIP 尚未上传完整。" });
                session.State = "processing";
                session.Updated = DateTimeOffset.UtcNow;
            }
            finally { session.Gate.Release(); }
            _ = Task.Run(async () =>
            {
                string? error = null;
                try
                {
                    await store.PublishArchiveFileAsync(session.ProgramId, session.Version, session.Path,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    app.Logger.LogError(ex, "Failed to publish uploaded release {ProgramId} {Version}",
                        session.ProgramId, session.Version);
                    error = ex is ArgumentException or InvalidOperationException or InvalidDataException
                        ? ex.Message : "服务器处理 ZIP 失败，请检查磁盘空间和服务端日志。";
                }
                finally
                {
                    try { File.Delete(session.Path); }
                    catch (IOException ex) { app.Logger.LogWarning(ex, "Could not remove upload temporary file"); }
                    await session.Gate.WaitAsync();
                    try
                    {
                        session.Error = error;
                        session.State = error is null ? "published" : "failed";
                        session.Updated = DateTimeOffset.UtcNow;
                    }
                    finally { session.Gate.Release(); }
                }
            });
            return Results.Accepted($"/admin/uploads/{id}");
        }).RequireAuthorization();

        app.MapGet("/admin/uploads/{id}", async (string id, HttpContext context) =>
        {
            if (!TryUpload(id, context, out var session)) return Results.NotFound();
            await session.Gate.WaitAsync(context.RequestAborted);
            try { return Results.Ok(new { state = session.State, received = session.Received, error = session.Error }); }
            finally { session.Gate.Release(); }
        }).RequireAuthorization();

        app.MapPost("/admin/releases", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = 1024L * 1024 * 1024 + 1024 * 1024;
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                var form = await context.Request.ReadFormAsync();
                var package = form.Files.GetFile("package");
                if (package is null || package.Length == 0 || package.Length > 1024L * 1024 * 1024)
                    throw new ArgumentException("请选择不超过 1 GB 的 ZIP 文件。");
                await using var stream = package.OpenReadStream();
                await store.PublishArchiveAsync(form["programId"].ToString(), form["version"].ToString(), stream, context.RequestAborted);
                return Results.Redirect("/admin?notice=published");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (InvalidOperationException ex) { return Error(ex.Message); }
            catch (InvalidDataException) { return Error("ZIP 文件无法读取或格式无效。"); }
        }).RequireAuthorization();

        app.MapPost("/admin/programs/{id}/delete", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                store.DeleteProgram(id);
                return Results.Redirect("/admin?notice=removed");
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (IOException) { return Error("程序文件正在被使用，请等下载结束后再删除。"); }
            catch (UnauthorizedAccessException) { return Error("程序文件无法删除，请检查服务器目录权限。"); }
        }).RequireAuthorization();
    }

    // Expiry accepts a datetime-local value (server local time, minute precision) and
    // still understands a bare date, which keeps meaning "the end of that day".
    private static DateTimeOffset? ParseExpiry(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        text = text.Replace(' ', 'T');
        if (DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return FromLocal(local);
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return FromLocal(date.ToDateTime(new TimeOnly(23, 59)));
        throw new ArgumentException("请输入有效的到期时间，例如 2026-11-08 18:30。");
    }

    private static DateTimeOffset FromLocal(DateTime local)
    {
        if (Zone.IsInvalidTime(local))
            throw new ArgumentException("这个时间在本地时区不存在（夏令时切换），请换一个时间。");
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string ExpiryField(IFormCollection form) =>
        form.ContainsKey("expiry") ? form["expiry"].ToString() : form["expiryDate"].ToString();

    private static string LocalText(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string UtcText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string InputText(DateTimeOffset? value) =>
        value is null ? "" : TimeZoneInfo.ConvertTime(value.Value, Zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    private static string ZoneLabel()
    {
        var offset = Zone.GetUtcOffset(DateTimeOffset.UtcNow);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{Zone.Id} · UTC{sign}{offset.Duration():hh\\:mm}";
    }

    private static string ZoneHint() =>
        $"<p class='hint'>到期时间以服务器本地时间填写和显示：{Encode(ZoneLabel())}，两者是同一时刻，客户端登录时按 UTC 校验。</p>";

    private static string ExpiryHint(UserSummary user)
    {
        var zone = Encode(ZoneLabel());
        if (user.ExpiresAt is null)
            return $"<p class='hint'>当前：永久。填写后按 {zone} 保存，精确到分钟；留空仍表示永久。</p>";
        var expired = user.ExpiresAt <= DateTimeOffset.UtcNow;
        return $"<p class='hint'>当前：{Encode(LocalText(user.ExpiresAt.Value))}（{zone}，对应 {Encode(UtcText(user.ExpiresAt.Value))}）{(expired ? "，已到期" : "")}。留空表示永久；填过去的时间可立即结束该账号。</p>";
    }

    private static async Task<bool> ValidCsrf(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    private static bool TryUpload(string id, HttpContext context, out UploadSession session)
    {
        if (Uploads.TryGetValue(id, out var found) &&
            found.Owner == context.User.Identity?.Name)
        {
            session = found;
            return true;
        }
        session = null!;
        return false;
    }

    private static void PruneUploads(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, session) in Uploads)
            if (session.State != "processing" && now - session.Updated >
                (session.State == "uploading" ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1)))
                Uploads.TryRemove(id, out _);
        foreach (var path in Directory.EnumerateFiles(directory, "*.zip"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Uploads.ContainsKey(id) && File.GetLastWriteTimeUtc(path) < now.AddDays(-1).UtcDateTime)
                try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string Csrf(string token) => $"<input type='hidden' name='__RequestVerificationToken' value='{Encode(token)}'>";
    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? "");
    private static string? EmptyToNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static IResult Html(string html, int statusCode = 200) => Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    private static IResult Error(string message) => Results.Redirect("/admin?error=" + Uri.EscapeDataString(message));

    private static void Metric(StringBuilder body, string label, long value, string id) =>
        body.Append("<div class='metric'><span>").Append(label).Append("</span><strong id='")
            .Append(id).Append("'>").Append(value).Append("</strong></div>");

    private static void Chart(StringBuilder body, long[] values)
    {
        var max = Math.Max(1, values.Max());
        foreach (var value in values)
            body.Append("<i class='bar' title='").Append(value).Append(" 次' style='height:")
                .Append(Math.Max(3, value * 100 / max)).Append("%'></i>");
    }

    private static string ModeOptions(string selected)
    {
        var labels = new[] { ("all", "全部稳定程序"), ("single", "单程序"), ("multiple", "多程序"), ("none", "未授权任何程序") };
        var body = new StringBuilder();
        foreach (var (id, label) in labels)
            body.Append("<option value='").Append(id).Append("'")
                .Append(selected == id ? " selected" : "").Append(">").Append(label).Append("</option>");
        return body.ToString();
    }

    private static string ProgramChoices(IReadOnlyList<ProgramInfo> programs, IEnumerable<string> selected)
    {
        if (!programs.Any(p => !p.IsBeta)) return "<em>请先登记稳定程序。</em>";
        var ids = selected.ToHashSet(StringComparer.Ordinal);
        var body = new StringBuilder();
        foreach (var program in programs.Where(p => !p.IsBeta))
            body.Append("<label class='check'><input type='checkbox' name='programIds' value='")
                .Append(Encode(program.Id)).Append("'").Append(ids.Contains(program.Id) ? " checked" : "")
                .Append(">").Append(Encode(program.Name)).Append("</label>");
        return body.ToString();
    }

    private static string ParentOptions(IReadOnlyList<ProgramInfo> programs, string? selected, string? currentId)
    {
        var body = new StringBuilder("<option value=''>不适用（稳定程序）</option>");
        foreach (var program in programs.Where(p => !p.IsBeta && p.Id != currentId))
            body.Append("<option value='").Append(Encode(program.Id)).Append("'")
                .Append(selected == program.Id ? " selected" : "").Append(">")
                .Append(Encode(program.Name)).Append("</option>");
        return body.ToString();
    }

    private static string AccessLabel(UserSummary user, IReadOnlyList<ProgramInfo> programs)
    {
        var scope = user.AccessMode switch
        {
            "single" => "单程序 · " + Names(user.ProgramIds, programs),
            "multiple" => "多程序 · " + Names(user.ProgramIds, programs),
            "none" => "未授权任何程序",
            _ => "全部稳定程序"
        };
        return user.BetaAccess ? scope + " + Beta" : scope;
    }

    private static string Names(IEnumerable<string> ids, IReadOnlyList<ProgramInfo> programs)
    {
        var names = ids.Select(id => programs.FirstOrDefault(p => p.Id == id)?.Name ?? id + "（已删除）").ToArray();
        return names.Length == 0 ? "未选择" : string.Join("、", names);
    }

    private static string IconHtml(string icon) =>
        icon.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            ? "<img class='appicon' alt='' src='" + Encode(icon) + "'>"
            : "<span class='appicon'>" + Encode(icon) + "</span>";

    private static async Task<string> ReadPngIcon(IFormFile file)
    {
        if (file.Length > 128 * 1024 || file.Length < 24)
            throw new ArgumentException("裁剪后的 PNG 图标不能超过 128 KB，请在预览窗口重新确认图片。");
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (!bytes.AsSpan(0, 8).SequenceEqual(signature) || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("请先在预览窗口完成裁剪，系统会自动转换为 PNG 图标。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 1 or > 512 || height is < 1 or > 512)
            throw new ArgumentException("PNG 图标尺寸应不超过 512×512。");
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    private static string Page(string title, string body)
    {
        if (!body.Contains("class='sidebar'", StringComparison.Ordinal))
            body = "<main class='auth-shell'><div class='auth-intro'><img class='auth-logo' src='/admin-assets/chispa.svg' alt='Chispa 闪光标识'><p class='eyebrow'>CHISPA / CONTROL CENTER</p><h1>让每一束闪光，<br>井然有序。</h1><p>你的程序分发，从这里开始。</p><div class='auth-footer'>A LITTLE SPARK. A NEW START.</div></div><div class='card auth-card'>" + body + "</div></main>";
        return $"""
            <!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{Encode(title)} · Chispa</title><link rel="icon" type="image/svg+xml" href="/admin-assets/chispa.svg">
            <link rel="stylesheet" href="/admin-assets/style.css"><script src="/admin-assets/app.js" defer></script>
            </head><body>{body}</body></html>
            """;
    }
}
