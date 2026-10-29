using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Chispa.AdminSecurity;

static partial class AdminPages
{
    private static string OperationsSection() => """
        <section id='overview' data-view='overview' class='card operations-card'>
          <div class='sectionhead'><h2>在线程序与流量</h2><button type='button' class='secondary' id='refreshOperations'>立即刷新</button></div>
          <div class='statgrid'>
            <div class='metric'><span>在线用户</span><strong id='onlineUsers'>—</strong></div>
            <div class='metric'><span>运行中的程序</span><strong id='onlinePrograms'>—</strong></div>
            <div class='metric'><span>累计上传到服务器</span><strong id='receivedBytes'>—</strong><small id='receiveRate'>等待采样</small></div>
            <div class='metric'><span>累计从服务器下载</span><strong id='sentBytes'>—</strong><small id='sendRate'>等待采样</small></div>
          </div>
          <p class='hint'>在线需租约有效，且最近两个心跳周期（另加 15 秒容错）内有活动；退出、超时或授权失效后移除。流量为本服务实际读取 / 写出的 HTTP 正文，含管理后台，不含 HTTP 头、TLS 开销和其他进程，重启清零。速率为两次采样间平均值。</p>
          <div class='table-scroll'><table class='monitor-table'><thead><tr><th>用户</th><th>程序 / 版本</th><th>来源地址</th><th>启动时间</th><th>最近心跳</th></tr></thead><tbody id='onlineRows'><tr><td colspan='5'>正在读取在线状态…</td></tr></tbody></table></div>
          <p class='hint' id='onlineStatus' role='status'>每 15 秒自动刷新</p>
        </section>
        """;

    private static string BillingControls(ProgramInfo program, string token)
    {
        if (program.IsBeta) return "<div class='billing-panel'><p class='hint'>计费和补偿跟随所属稳定程序：" + Encode(program.ParentProgramId) + "</p></div>";
        var path = "/admin/programs/" + Uri.EscapeDataString(program.Id);
        var paused = program.BillingPausedAt is not null;
        return $"""
            <div class='billing-panel'><div class='sectionhead'><h3>计费管理</h3><span class='badge'>{(paused ? "已暂停计费" : "正常计时")}</span></div>
            <p class='hint'>{(paused ? "暂停开始：" + Encode(LocalText(program.BillingPausedAt!.Value)) + "。" : "")}暂停期间仍可使用，剩余时长冻结；Beta 同步。补偿仅作用于当前有效的限时授权，不改变永久或已到期授权。</p>
            <div class='billing-actions'><form method='post' action='{path}/billing' data-confirm='{(paused ? "恢复此程序计时？" : "暂停此程序所有有效授权的计时？程序仍可使用。")}'>{Csrf(token)}
              <input type='hidden' name='paused' value='{(paused ? "0" : "1")}'><button class='secondary'>{(paused ? "恢复计费" : "暂停计费")}</button></form>
            <form method='post' action='{path}/compensate' data-confirm='将按所填时长补偿此程序当前有效的全部限时授权，确认执行？'>{Csrf(token)}
              <label>补偿分钟数<input name='minutes' type='number' min='1' max='525600' value='60' required></label><button>补偿时间</button></form></div></div>
            """;
    }

    public static void MapOperationsAdmin(this WebApplication app, Store store, LaunchGate launch, AdminGuard guard)
    {
        app.MapGet("/admin/online", () =>
        {
            var programs = launch.OnlinePrograms();
            return Results.Ok(new { sampledAt = DateTimeOffset.UtcNow,
                onlineUsers = programs.Select(p => p.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(), programs });
        }).RequireAuthorization();

        app.MapPost("/admin/programs/{id}/billing", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                var form = await context.Request.ReadFormAsync();
                if (form["paused"] != "0" && form["paused"] != "1") throw new ArgumentException("无效的计费状态。");
                var paused = form["paused"] == "1";
                store.SetBillingPaused(id, paused);
                guard.Audit(paused ? "billing-pause" : "billing-resume", context.User.Identity?.Name ?? "", id, "ok", context);
                return Results.Redirect("/admin?notice=updated#programs");
            }
            catch (ArgumentException ex) { return Results.Redirect("/admin?error=" + Uri.EscapeDataString(ex.Message) + "#programs"); }
        }).RequireAuthorization();

        app.MapPost("/admin/programs/{id}/compensate", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                var form = await context.Request.ReadFormAsync();
                if (!int.TryParse(form["minutes"], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                    throw new ArgumentException("请输入有效的补偿分钟数。");
                var count = store.CompensateProgram(id, minutes);
                guard.Audit("billing-compensate", context.User.Identity?.Name ?? "", id, $"ok minutes={minutes} accounts={count}", context);
                return Results.Redirect("/admin?notice=compensated&count=" + count + "#programs");
            }
            catch (ArgumentException ex) { return Results.Redirect("/admin?error=" + Uri.EscapeDataString(ex.Message) + "#programs"); }
        }).RequireAuthorization();
    }
}
