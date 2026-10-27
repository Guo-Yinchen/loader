using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chispa.AdminSecurity;

// Re-authentication and rate limiting for the parts of the admin panel that expose a secret.
//
// "View the user's password" is a deliberate product requirement, so it is not going away. What can
// be made safe is the path to it:
//   * a fresh, server-side proof of identity before the secret is shown (not a browser dialog, not
//     a checkbox: the admin types their own password and the server decides);
//   * that proof becomes a short-lived, single-admin, few-use grant - so the second click does not
//     have to be believed just because the first one was;
//   * every attempt is rate limited and every outcome is audited.
// The password itself, the ciphertext and the response body are never written anywhere.
sealed class AdminGuard
{
    public enum GrantCheck { Valid, Missing, Expired, Foreign }

    private sealed record Grant(string Admin, string Session, string Target, DateTimeOffset ExpiresAt, int UsesLeft);
    private sealed record Window(DateTimeOffset Until, int Count, string Key);

    private readonly ConcurrentDictionary<string, Grant> grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Window> stepUpFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Window> viewWindows = new(StringComparer.Ordinal);
    private readonly object auditGate = new();
    private readonly string auditPath;
    private readonly ILogger logger;

    public int StepUpSeconds { get; }
    public int StepUpUses { get; }
    public int StepUpFailuresPerWindow { get; }
    public int ViewsPerWindow { get; }
    public IReadOnlyList<IPNetwork> AllowedNetworks { get; }

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ViewWindow = TimeSpan.FromMinutes(10);

    public AdminGuard(string dataRoot, ILogger logger)
    {
        this.logger = logger;
        auditPath = Path.Combine(dataRoot, "admin-audit.log");
        StepUpSeconds = Bound("CHISPA_ADMIN_STEPUP_SECONDS", 120, 30, 600);
        StepUpUses = Bound("CHISPA_ADMIN_STEPUP_USES", 3, 1, 20);
        StepUpFailuresPerWindow = Bound("CHISPA_ADMIN_STEPUP_FAILURES", 5, 2, 50);
        ViewsPerWindow = Bound("CHISPA_ADMIN_VIEWS", 20, 1, 500);
        AllowedNetworks = ParseNetworks(Environment.GetEnvironmentVariable("CHISPA_ADMIN_ALLOWED_NETWORKS"));
    }

    private static int Bound(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum) : fallback;

    private static IReadOnlyList<IPNetwork> ParseNetworks(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return [];
        var networks = new List<IPNetwork>();
        foreach (var raw in configured.Split([',', ';', ' ', '\t', '\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(raw, out var address))
                networks.Add(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else if (IPNetwork.TryParse(raw, out var network))
                networks.Add(network);
            else
                throw new InvalidOperationException(
                    $"CHISPA_ADMIN_ALLOWED_NETWORKS contains '{raw}', which is neither an IP address nor a CIDR range.");
        }
        return networks;
    }

    /// <summary>Loopback is always allowed: a misconfigured restriction must never lock the
    /// operator out of the machine the service runs on.</summary>
    public bool NetworkAllowed(IPAddress? address)
    {
        if (AllowedNetworks.Count == 0) return true;
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        foreach (var network in AllowedNetworks)
            if (network.Contains(address)) return true;
        return false;
    }

    // ---- step-up grants ------------------------------------------------------------------------
    public string IssueGrant(string admin, string session, string target)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        grants[token] = new Grant(admin, session, target, DateTimeOffset.UtcNow.AddSeconds(StepUpSeconds), StepUpUses);
        Prune();
        return token;
    }

    public static string SessionBinding(HttpContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            context.Request.Cookies["chispa_admin"] ?? "")));

    public GrantCheck TryConsumeGrant(string admin, string session, string target, string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return GrantCheck.Missing;
        while (grants.TryGetValue(token, out var grant))
        {
            if (!string.Equals(grant.Admin, admin, StringComparison.Ordinal) ||
                grant.Session != session || !string.Equals(grant.Target, target, StringComparison.OrdinalIgnoreCase))
                return GrantCheck.Foreign;
            if (grant.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                grants.TryRemove(new KeyValuePair<string, Grant>(token, grant));
                return GrantCheck.Expired;
            }
            // Consume the exact value read: concurrent views must not share the final use.
            if (grant.UsesLeft <= 1)
            {
                if (grants.TryRemove(new KeyValuePair<string, Grant>(token, grant))) return GrantCheck.Valid;
            }
            else if (grants.TryUpdate(token, grant with { UsesLeft = grant.UsesLeft - 1 }, grant))
                return GrantCheck.Valid;
        }
        return GrantCheck.Missing;
    }

    // ---- rate limiting -------------------------------------------------------------------------
    public bool StepUpBlocked(string admin, string ip) =>
        stepUpFailures.TryGetValue(admin + "|" + ip, out var window) &&
        window.Until > DateTimeOffset.UtcNow && window.Count >= StepUpFailuresPerWindow;

    public void RecordStepUpFailure(string admin, string ip)
    {
        var now = DateTimeOffset.UtcNow;
        var key = admin + "|" + ip;
        stepUpFailures.AddOrUpdate(key, _ => new Window(now + FailureWindow, 1, key),
            (_, old) => old.Until <= now ? new Window(now + FailureWindow, 1, key) : old with { Count = old.Count + 1 });
    }

    public void ClearStepUpFailures(string admin, string ip) => stepUpFailures.TryRemove(admin + "|" + ip, out _);

    public bool ViewBlocked(string admin) =>
        viewWindows.TryGetValue("view|" + admin, out var window) &&
        window.Until > DateTimeOffset.UtcNow && window.Count >= ViewsPerWindow;

    public void RecordView(string admin)
    {
        var now = DateTimeOffset.UtcNow;
        var key = "view|" + admin;
        viewWindows.AddOrUpdate(key, _ => new Window(now + ViewWindow, 1, key),
            (_, old) => old.Until <= now ? new Window(now + ViewWindow, 1, key) : old with { Count = old.Count + 1 });
    }

    public void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (token, grant) in grants)
            if (grant.ExpiresAt <= now) grants.TryRemove(token, out _);
        foreach (var (key, window) in stepUpFailures)
            if (window.Until <= now) stepUpFailures.TryRemove(key, out _);
        foreach (var (key, window) in viewWindows)
            if (window.Until <= now) viewWindows.TryRemove(key, out _);
    }

    // ---- audit ----------------------------------------------------------------------------------
    /// <summary>One JSON line per event. Only identity, action, target, outcome and address:
    /// never a password, a ciphertext, a token or a response body.</summary>
    public void Audit(string evt, string admin, string? target, string result, HttpContext? context)
    {
        var entry = new
        {
            at = DateTimeOffset.UtcNow.ToString("o"),
            eventName = evt,
            admin,
            target,
            result,
            ip = context?.Connection.RemoteIpAddress?.ToString(),
        };
        var line = JsonSerializer.Serialize(entry);
        lock (auditGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
                if (File.Exists(auditPath) && new FileInfo(auditPath).Length > 8L * 1024 * 1024)
                    File.Move(auditPath, auditPath + ".1", overwrite: true);
                File.AppendAllText(auditPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // Auditing must not take the admin panel down; the console log still has the line.
                logger.LogWarning("admin audit could not be written: {Reason}", ex.Message);
            }
        }
        logger.LogInformation("admin audit {Event} admin={Admin} target={Target} result={Result} ip={Ip}",
            evt, admin, target ?? "-", result, context?.Connection.RemoteIpAddress?.ToString() ?? "-");
    }
}
