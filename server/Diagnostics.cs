using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

sealed record DiagnosticRequest(string? Id, string? Summary, string? Logs);
sealed record DiagnosticOwner(string Account, string Program, string Version);
sealed record DiagnosticRecord(string Id, DateTimeOffset ReceivedAt, string Account, string Program,
    string Version, string Summary, string Logs);

// Bounded files on disk, no in-memory report archive. The account/build come from
// a live launch lease, never from client-supplied metadata or file names.
sealed class DiagnosticStore(string dataRoot)
{
    private readonly object gate = new();
    private readonly string directory = Path.Combine(dataRoot, "diagnostics");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Account names may contain '--' or email punctuation: do not use them as
    // path components or split them to establish retention ownership.
    private static string AccountKey(string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.ToUpperInvariant()))).ToLowerInvariant();
    private IEnumerable<FileInfo> Files() => Directory.Exists(directory)
        ? new DirectoryInfo(directory).EnumerateFiles("*.json") : [];

    public (int Status, object Body) Save(DiagnosticOwner owner, DiagnosticRequest? request)
    {
        if (request is null || !Guid.TryParseExact(request.Id, "N", out var id) ||
            string.IsNullOrWhiteSpace(request.Summary) || request.Summary.Length > 2048 ||
            request.Logs is null || Encoding.UTF8.GetByteCount(request.Logs) > 16 * 1024 ||
            Encoding.UTF8.GetByteCount(request.Summary) > 8 * 1024)
            return (400, new { code = "invalid_diagnostic" });
        lock (gate)
        {
            Directory.CreateDirectory(directory);
            PruneLocked();
            var prefix = AccountKey(owner.Account) + "--";
            var name = prefix + id.ToString("N") + ".json";
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return (200, new { id = id.ToString("N"), duplicate = true });
            var own = Files().Where(f => f.Name.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (own.Any(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-1)) ||
                own.Count(f => f.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) >= 10)
                return (429, new { code = "diagnostic_rate_limited" });
            var record = new DiagnosticRecord(id.ToString("N"), DateTimeOffset.UtcNow,
                owner.Account, owner.Program, owner.Version, request.Summary, request.Logs);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            if (bytes.Length > 64 * 1024) return (413, new { code = "diagnostic_too_large" });
            // Atomic rename means an interrupted write cannot become a visible report.
            var temporary = path + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            PruneLocked();
            return (201, new { id = record.Id });
        }
    }

    public DiagnosticRecord[] List(string? account)
    {
        lock (gate)
        {
            return Files().Where(f => string.IsNullOrEmpty(account) ||
                    f.Name.StartsWith(AccountKey(account) + "--", StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(100)
                .Select(Read).OfType<DiagnosticRecord>().ToArray();
        }
    }
    private static DiagnosticRecord? Read(FileInfo file)
    {
        if (file.Length > 64 * 1024) return null;
        try { return JsonSerializer.Deserialize<DiagnosticRecord>(File.ReadAllBytes(file.FullName), Json); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public void Prune() { lock (gate) PruneLocked(); }
    private void PruneLocked()
    {
        var files = Files().OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int retained = 0;
        foreach (var file in files)
        {
            var account = file.Name.Split("--", 2)[0];
            counts.TryGetValue(account, out var count);
            if (retained >= 500 || count >= 10 || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) file.Delete();
            else { counts[account] = count + 1; ++retained; }
        }
    }
}

static partial class AdminPages
{
    public static void MapDiagnostics(this WebApplication app, DiagnosticStore reports)
    {
        app.MapGet("/admin/diagnostics", (HttpContext context) =>
        {
            var account = context.Request.Query["account"].ToString();
            var body = new StringBuilder("<a href='/admin'>← 返回管理后台</a><h1>输入故障报告</h1><p>自动上传故障摘要和最多 16 KiB 的相关诊断日志。保留 7 天，每账号最多 10 份，全局最多 500 份；下方最多显示最近 100 份。</p>");
            body.Append("<form method='get'><label>按账号筛选<input name='account' maxlength='32' value='")
                .Append(Encode(account)).Append("'></label><button>查询</button></form>");
            var records = reports.List(account);
            if (records.Length == 0) body.Append("<p>暂无匹配报告。</p>");
            foreach (var report in records)
            {
                body.Append("<article class='card'><h2>").Append(Encode(report.Account)).Append(" · ")
                    .Append(Encode(report.Program)).Append(" ").Append(Encode(report.Version)).Append("</h2><p>")
                    .Append(Encode(report.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")))
                    .Append(" · ").Append(Encode(report.Id)).Append("</p><pre style='white-space:pre-wrap;overflow-wrap:anywhere'>")
                    .Append(Encode(report.Summary)).Append("</pre><details><summary>查看相关日志（时间为程序运行所在系统的启动后毫秒数）</summary><pre style='white-space:pre-wrap;overflow-wrap:anywhere'>")
                    .Append(Encode(report.Logs)).Append("</pre></details></article>");
            }
            return Html(Page("输入故障报告", body.ToString()));
        }).RequireAuthorization();
    }
}
