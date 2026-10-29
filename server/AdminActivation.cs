using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Chispa.AdminSecurity;

static partial class AdminPages
{
    private static string ActivationSection(Store store, IReadOnlyList<ProgramInfo> programs, string token)
    {
        var body = new StringBuilder("""
            <section id='activation' data-view='activation' class='card'><details class='create-panel'><summary>生成激活码<span>选择程序范围、时长与数量</span></summary>
            <p class='hint'>不同范围的激活码可混合兑换，各程序分别从其到期时间或现在中较晚者起算。全部程序码为兑换时已登记的每个稳定程序各增加所选时长。Beta 通行证不增加时长，只解锁已有稳定授权对应的 Beta。完整激活码仅在生成时下载一次，请妥善保存。</p>
            <form method='post' action='/admin/activation/generate' data-download='true'>
            """).Append(Csrf(token)).Append("""
            <div class='fields'><label>生成数量<input name='count' type='number' min='1' max='100' value='1' required></label>
            <label>授权时长<select name='days'><option value='1'>1 天</option><option value='7'>7 天</option><option value='30' selected>30 天</option><option value='90'>90 天</option><option value='365'>365 天</option><option value='0'>永久</option></select></label>
            <label>兑换截止时间（可留空）<input name='redeemBefore' type='datetime-local'></label></div>
            <label>程序范围<select name='accessMode'><option value='all'>全部稳定程序</option><option value='single'>单程序</option><option value='multiple'>多程序</option><option value='beta'>仅 Beta 通行证（不计时）</option></select></label>
            <div class='choices'><span>指定稳定程序</span>
            """).Append(ProgramChoices(programs, [])).Append("""
            </div><label class='check'><input type='checkbox' name='betaAccess' value='1'>允许访问 Beta 程序</label>
            <p class='hint'>兑换截止时间使用服务器本地时间。截止时间与账户的授权时长相互独立。</p>
            <button>生成并下载 CSV</button><p role='status' data-download-status></p></form></details></section>
            <section data-view='activation' class='card'><h2>激活码记录</h2><p class='hint'>按程序范围 → 授权时长展开，快速定位卡密。</p><p class='hint'>只显示编号及尾号，不保存可再次导出的完整激活码。</p>
            """);
        var keys = store.ListActivationKeys();
        if (keys.Count == 0) body.Append("<p class='empty-state'>尚未生成激活码。</p>");
        foreach (var scope in keys.GroupBy(k => k.AccessMode + ":" + string.Join("|", k.ProgramIds.Order(StringComparer.Ordinal))))
        {
            var first = scope.First();
            var scopeName = first.AccessMode == "all" ? "全部稳定程序" : first.AccessMode == "beta" ? "Beta 通行证" : Names(first.ProgramIds.Order(StringComparer.Ordinal), programs);
            body.Append("<details class='key-scope'><summary>").Append(Encode(scopeName)).Append("<span class='badge'>").Append(scope.Count()).Append(" 张</span></summary>");
            foreach (var duration in scope.GroupBy(k => k.AccessMode == "beta" ? -1 : k.Days).OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key))
            {
                body.Append("<details class='key-duration'><summary>").Append(duration.Key == -1 ? "不计时通行证" : duration.Key == 0 ? "永久" : duration.Key + " 天")
                    .Append("<span class='badge'>").Append(duration.Count(k => k.State == "unused" && (k.RedeemBefore is null || k.RedeemBefore > DateTimeOffset.UtcNow)))
                    .Append(" 可用 / ").Append(duration.Count()).Append(" 总数</span></summary>");
                foreach (var key in duration)
                {
                    var state = key.State == "used" ? "已使用" : key.State == "revoked" ? "已作废" :
                        key.RedeemBefore <= DateTimeOffset.UtcNow ? "兑换已过期" : "未使用";
                    body.Append("<details class='user'><summary class='userhead'><strong>…")
                        .Append(Encode(key.Hint)).Append("</strong><span>").Append(key.AccessMode == "beta" ? "通行证" : key.Days == 0 ? "永久" : key.Days + " 天")
                        .Append("</span><span class='badge'>").Append(state).Append("</span><span>")
                        .Append(Encode(key.UsedBy ?? "")).Append("</span></summary><div class='user-editor'><p>编号：")
                        .Append(Encode(key.Id)).Append("</p><p>权限：")
                        .Append(Encode(key.AccessMode == "beta" ? "Beta 通行证" : key.AccessMode == "all" ? "全部稳定程序" : string.Join("、", key.ProgramIds)))
                        .Append(key.BetaAccess && key.AccessMode != "beta" ? " + Beta" : "").Append("</p><p>生成时间：")
                        .Append(Encode(LocalText(key.CreatedAt))).Append("；兑换截止：")
                        .Append(Encode(key.RedeemBefore is { } cutoff ? LocalText(cutoff) : "不限"))
                        .Append("；使用时间：").Append(Encode(key.UsedAt is { } used ? LocalText(used) : "—")).Append("</p>");
                    if (key.State == "unused") body.Append("<form method='post' action='/admin/activation/")
                        .Append(key.Id).Append("/revoke' data-confirm='确定作废此激活码？'>").Append(Csrf(token))
                        .Append("<button class='danger'>作废激活码</button></form>");
                    body.Append("</div></details>");
                }
                body.Append("</details>");
            }
            body.Append("</details>");
        }
        return body.Append("</section>").ToString();
    }

    public static void MapActivationAdmin(this WebApplication app, Store store, AdminGuard guard)
    {
        app.MapPost("/admin/activation/generate", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest(new { error = "表单已过期，请刷新页面。" });
            try
            {
                var form = await context.Request.ReadFormAsync();
                if (!int.TryParse(form["count"], out var count) || !int.TryParse(form["days"], out var days))
                    throw new ArgumentException("请输入正确的生成数量和时长。");
                var batch = store.GenerateActivationKeys(count, days, form["accessMode"].ToString(),
                    form["programIds"].Select(v => v!), form["betaAccess"] == "1", ParseExpiry(form["redeemBefore"]));
                guard.Audit("activation-generate", context.User.Identity?.Name ?? "", count.ToString(), "ok", context);
                var csv = new StringBuilder("code,id,days,accessMode,programIds,betaAccess,redeemBeforeUtc\r\n");
                foreach (var item in batch) csv.Append(item.Code).Append(',').Append(item.Key.Id).Append(',')
                    .Append(item.Key.Days).Append(',').Append(item.Key.AccessMode).Append(',')
                    .Append(string.Join("|", item.Key.ProgramIds)).Append(',').Append(item.Key.BetaAccess).Append(',')
                    .Append(item.Key.RedeemBefore?.ToString("O")).Append("\r\n");
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8",
                    $"chispa-activation-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).RequireAuthorization();

        app.MapPost("/admin/activation/{id}/revoke", async (string id, HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidCsrf(context, antiforgery)) return Results.BadRequest("Invalid form token.");
            try
            {
                store.RevokeActivationKey(id);
                guard.Audit("activation-revoke", context.User.Identity?.Name ?? "", id, "ok", context);
                return Results.Redirect("/admin?notice=updated#activation");
            }
            catch (ArgumentException ex) { return Results.Redirect("/admin?error=" + Uri.EscapeDataString(ex.Message) + "#activation"); }
        }).RequireAuthorization();
    }
}
