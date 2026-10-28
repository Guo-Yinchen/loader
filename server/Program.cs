using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chispa.AdminSecurity;
using Chispa.Limits;
using Chispa.Deployment;
using Chispa.Release;
using Chispa.StoreInternal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var defaultDataRoot = File.Exists(Path.Combine(projectRoot, "run-local.ps1"))
    ? Path.Combine(projectRoot, ".local", "server")
    : Path.Combine(AppContext.BaseDirectory, "data");
var dataRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("CHISPA_SERVER_DATA") ?? defaultDataRoot);
var store = new Store(dataRoot);

if (args.Length > 0)
{
    try
    {
        switch (args[0])
        {
            case "user-add" when args.Length == 2:
                Console.Write("Password (at least 12 characters): ");
                var password = ReadPassword();
                store.AddUser(args[1], password);
                Console.WriteLine("User created.");
                return;
            case "admin-create" when args.Length == 2:
                Console.Write("Admin password (at least 12 characters): ");
                store.AddAdmin(args[1], ReadPassword());
                Console.WriteLine("Administrator created. Open /admin in a browser.");
                return;
            case "admin-reset" when args.Length == 2:
                Console.Write("New admin password (at least 12 characters): ");
                store.ResetAdmin(args[1], ReadPassword());
                Console.WriteLine("Administrator password reset.");
                return;
            case "program-add" when args.Length is 4 or 6:
                if (args.Length == 6 && args[4] != "beta")
                    throw new ArgumentException("Use beta <parent-program-id> for a Beta option.");
                store.AddProgram(args[1], args[2], args[3], args.Length == 6,
                    parentProgramId: args.Length == 6 ? args[5] : null);
                Console.WriteLine("Program registered.");
                return;
            case "program-link-beta" when args.Length == 3:
                var betaProgram = store.FindProgram(args[1]) ?? throw new ArgumentException("Program not found.");
                store.UpdateProgram(betaProgram.Id, betaProgram.Name, betaProgram.Icon, true, args[2]);
                Console.WriteLine("Beta program linked.");
                return;
            case "publish" when args.Length == 5:
                // Publishing is signed-only: the package and the signature over it are produced in
                // the release environment by chispa-sign and this command only verifies and stores.
                var published = store.PublishPackage(args[1], args[2], args[3], args[4]);
                Console.WriteLine($"Published {published.ProgramId} {published.Version} ({published.Size} bytes, " +
                    $"SHA-256 {published.Sha256}, keyId {published.SignatureKeyId}).");
                return;
            case "sign-release" when args.Length == 4:
                // Migration for releases published before signatures existed: the package stays as
                // it is, and the signature that covers it is attached after the fact.
                var signed = store.SignExistingRelease(args[1], args[2], File.ReadAllText(args[3]));
                Console.WriteLine($"Signed existing release {signed.ProgramId} {signed.Version} " +
                    $"(SHA-256 {signed.Sha256}, keyId {signed.SignatureKeyId}).");
                return;
            case "trust-keys":
                foreach (var key in store.TrustKeys())
                    Console.WriteLine($"{key.KeyId} {key.PublicKeyBase64}");
                return;
            default:
                Console.Error.WriteLine("Usage: Chispa.Server [admin-create <username> | admin-reset <username> | user-add <username> | " +
                    "program-add <id> <display-name> <executable.exe> [beta <parent-id>] | program-link-beta <id> <parent-id> | " +
                    "publish <id> <version> <package.zip> <signature.json> | sign-release <id> <version> <signature.json> | trust-keys]");
                Environment.ExitCode = 2;
                return;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
        return;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("CHISPA_SERVER_URL") ?? "http://127.0.0.1:5115");
// ---- deployment configuration ---------------------------------------------------------------
// Read once, described in the log, never guessed. The production shape is documented in
// docs/task-04-deployment.md: TLS terminates in the tunnel client on this machine, so the service
// receives plain HTTP from loopback with X-Forwarded-* written by that client.

// Only the peers named here may set X-Forwarded-For / X-Forwarded-Proto. Empty means no header is
// believed at all, which is the honest default for a service bound to loopback.
var proxyTrust = ProxyTrust.Parse(Environment.GetEnvironmentVariable("CHISPA_TRUSTED_PROXIES"));

// Production turns on the cookie rules a public deployment needs. It cannot be inferred from the
// bind address: behind the tunnel this service still listens on 127.0.0.1, so "loopback" says
// nothing about who can reach it. Local development therefore stays explicitly unmarked.
var production = Environment.GetEnvironmentVariable("CHISPA_PRODUCTION") is "1" or "true" or "TRUE";

builder.WebHost.ConfigureKestrel(options =>
{
    // Release packages are uploaded straight from the admin page. Keep the body limit
    // aligned with the form limit and stop Kestrel's minimum data rate from aborting
    // slow uploads of large ZIP files.
    options.Limits.MaxRequestBodySize = 1024L * 1024 * 1024 + 8 * 1024 * 1024;
    options.Limits.MinRequestBodyDataRate = null;
    // "Server: Kestrel" tells a stranger which stack to aim at and nothing else.
    options.AddServerHeader = false;
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 1024L * 1024 * 1024);
if (proxyTrust.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options => proxyTrust.Apply(options));
}
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("CHISPA_LOG_LEVEL"), true,
    out var configuredLevel) ? configuredLevel : LogLevel.Information);
// Request-level chatter would fill the rotating file with one line per download chunk.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var logDirectory = Environment.GetEnvironmentVariable("CHISPA_LOG_DIRECTORY");
if (!string.IsNullOrWhiteSpace(logDirectory))
{
    var keepDays = int.TryParse(Environment.GetEnvironmentVariable("CHISPA_LOG_KEEP_DAYS"), out var days) ? days : 14;
    builder.Logging.AddProvider(new RotatingFileLoggerProvider(logDirectory, keepDays));
}
var keyDirectory = Path.Combine(dataRoot, "keys");
Directory.CreateDirectory(keyDirectory);
var protection = builder.Services.AddDataProtection()
    .SetApplicationName("Chispa.Server")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "chispa_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        // Production: the browser is talking to the tunnel over TLS, so the cookie must never be
        // sent on a plain connection - not even one that loops back to this machine. Local
        // development keeps SameAsRequest so `http://127.0.0.1:5115/admin` still works.
        options.Cookie.SecurePolicy = production ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/admin/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = async context =>
        {
            var name = context.Principal?.Identity?.Name;
            var hash = context.Principal?.FindFirst("admin-hash")?.Value;
            if (store.AdminSessionValid(name, hash)) return;
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "chispa_admin_csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = production ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    options.HeaderName = "X-CSRF-TOKEN";
});
var app = builder.Build();
var sessions = new ConcurrentDictionary<string, Session>();
var failedLogins = new ConcurrentDictionary<string, LoginFailures>();
var metrics = new ServerMetrics();
// Launch authorization is memory-only on purpose: it follows the lifetime of the sessions it
// descends from, so restarting this service forces every running program through the loader
// again instead of leaving a lease behind that nobody re-checked.
var launch = new LaunchGate(store, app.Logger);
// The readiness answer is cached so a checker polling every few seconds does not turn a health
// probe into disk load. The window is configurable because a test has to see the change now.
var readinessSeconds = int.TryParse(Environment.GetEnvironmentVariable("CHISPA_READINESS_CACHE_SECONDS"),
    out var configuredSeconds) ? Math.Clamp(configuredSeconds, 0, 300) : 10;
var readiness = new Readiness(dataRoot, app.Logger, TimeSpan.FromSeconds(readinessSeconds));

// Forwarded headers are applied only when CHISPA_TRUSTED_PROXIES names the peer, and only then.
// They must be read before anything that looks at the scheme or the client address: the HTTPS
// gate below, the admin cookie policy, the login rate limiter and the metrics all consume them.
if (proxyTrust.Enabled) app.UseForwardedHeaders();
app.Logger.LogInformation("deployment: production={Production} {ProxyTrust} logDirectory={LogDirectory}",
    production, proxyTrust.Describe(), string.IsNullOrWhiteSpace(logDirectory) ? "(console only)" : logDirectory);
if (production && !proxyTrust.Enabled)
    app.Logger.LogWarning("deployment: CHISPA_PRODUCTION is on but no trusted proxy is configured, so every " +
        "request looks like it came from {Loopback} and per-IP limits and logs cannot tell clients apart",
        System.Net.IPAddress.Loopback);

// Remote deployment must terminate TLS at the tunnel client or a trusted reverse proxy. With a
// trusted proxy in front, `IsHttps` and `RemoteIpAddress` are the effective values the proxy
// reported - the role of this gate is to refuse a plain-HTTP peer that was not vouched for.
//
// /health and /ready are exempt so a local watchdog can poll the loopback address without TLS.
// In production the loopback exemption for everything else is gone as well: an administrator on
// the box must use the public HTTPS address, not a plaintext local one.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var probeEndpoint = path.StartsWithSegments("/health") || path.StartsWithSegments("/ready");
    var loopback = System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None);
    if (!context.Request.IsHttps && !probeEndpoint && !(loopback && !production))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("HTTPS required");
        return;
    }
    await next();
});
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/admin"))
        context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    try { await next(); }
    finally { metrics.Record(context.Request.Path, context.Response.StatusCode); }
});
// Request budgets and body caps. Registered before authentication so a flood is rejected without
// touching cookies or the store.
var limits = new RequestLimits(app.Logger);
app.Use(async (context, next) =>
{
    if (!limits.TryEnter(context, out var status))
    {
        context.Response.StatusCode = status;
        context.Response.Headers.RetryAfter = "30";
        await context.Response.WriteAsync("Too many requests");
        return;
    }
    try { await next(); }
    finally { limits.Leave(context); }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Content("""
<!doctype html>
<html lang="zh-CN">
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Chispa 服务端</title>
<link rel="icon" href="/admin-assets/chispa.svg"><link rel="stylesheet" href="/admin-assets/style.css">
<main class="auth-shell"><div class="auth-intro"><img class="auth-logo" src="/admin-assets/chispa.svg" alt="Chispa">
<p class="eyebrow">CHISPA / DISTRIBUTION</p><h1>每一次启动，<br>都有新的闪光。</h1></div>
<section class="card"><span class="online">● 运行中</span><h2>Chispa 服务端正在运行</h2>
<p class="hint">在控制中心管理账号、程序与版本分发。</p>
<a class="button" href="/admin">打开管理后台 →</a>　<a href="/health">接口状态</a></section></main>
</html>
""", "text/html; charset=utf-8"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// Readiness for an external checker: 200 when this instance can actually serve, 503 when it cannot.
// The body carries a status word and nothing else - versions, paths and reasons stay in the log.
app.MapGet("/ready", () => readiness.IsReady()
    ? Results.Ok(new { status = "ready" })
    : Results.Json(new { status = "not-ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));

// Loader metadata: which loader build is current, and where a user can get it. Public because the
// login page needs it before there is a session. The download address is configuration, and it is
// validated here so a bad value can never reach the loader as something to run.
app.MapGet("/api/v1/launcher/latest", () =>
{
    var version = Environment.GetEnvironmentVariable("CHISPA_LAUNCHER_VERSION");
    if (string.IsNullOrWhiteSpace(version)) return Results.NotFound(new { code = "launcher_unknown" });
    var rawDownload = Environment.GetEnvironmentVariable("CHISPA_LAUNCHER_DOWNLOAD_URL") ?? "";
    var downloadUrl = LauncherUrlAllowed(rawDownload) ? rawDownload.Trim() : "";
    return Results.Ok(new
    {
        version = version.Trim(),
        minimumVersion = (Environment.GetEnvironmentVariable("CHISPA_LAUNCHER_MINIMUM") ?? "").Trim(),
        downloadUrl,
        notes = (Environment.GetEnvironmentVariable("CHISPA_LAUNCHER_NOTES") ?? "").Trim(),
        // What this service can actually do. A loader decides from these tokens, not from a version
        // string, and the server refuses flows a client has not declared support for.
        capabilities = new[] { "launch-auth/1", "release-signature/1", "launcher-update/1" },
    });
});

static bool LauncherUrlAllowed(string url)
{
    if (string.IsNullOrWhiteSpace(url) || url.Length > 500) return false;
    if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
    if (uri.Scheme == Uri.UriSchemeHttps) return true;
    // Loopback http is the local development case only; the loader applies the same rule.
    return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
}
app.MapGet("/api/v1/connectivity/client", (HttpContext context) =>
{
    metrics.ClientProbe(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    return Results.Ok(new { status = "ok", checkedAtUtc = DateTimeOffset.UtcNow });
});

app.MapPost("/api/v1/session", (Credentials? credentials, HttpContext context) =>
{
    if (credentials is null || string.IsNullOrWhiteSpace(credentials.Username) || credentials.Password is null)
        return Results.BadRequest(new { error = "Username and password required" });

    var key = $"{context.Connection.RemoteIpAddress}:{credentials.Username.ToLowerInvariant()}";
    var now = DateTimeOffset.UtcNow;
    if (failedLogins.TryGetValue(key, out var failures) && failures.Count >= 5 && failures.Until > now)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    var user = store.AuthenticateUser(credentials.Username, credentials.Password);
    if (user is null)
    {
        metrics.Login(false);
        failedLogins.AddOrUpdate(key, _ => new LoginFailures(1, now.AddMinutes(15)),
            (_, old) => old.Until <= now ? new LoginFailures(1, now.AddMinutes(15)) : old with { Count = old.Count + 1 });
        return Results.Unauthorized();
    }

    metrics.Login(true);
    failedLogins.TryRemove(key, out _);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var expires = now.AddHours(12);
    sessions[token] = new Session(user.Username, user.Hash, expires);
    return Results.Ok(new { token, expiresAt = expires, username = user.Username,
        accountExpiresAt = user.ExpiresAt, serverTime = DateTimeOffset.UtcNow });
});

// An expired subscription can still sign in and inspect its account. Execution and download
// endpoints continue to require an active subscription through CurrentUser's default policy.
app.MapGet("/api/v1/session", (HttpContext context) =>
{
    var user = CurrentUser(context, allowExpired: true);
    return user is null ? Results.Unauthorized() : Results.Ok(new {
        username = user.Username, accountExpiresAt = user.ExpiresAt, serverTime = DateTimeOffset.UtcNow,
        programExpiries = user.ProgramExpiries });
});

app.MapPost("/api/v1/register", (RegistrationRequest request) =>
{
    try
    {
        var user = store.RegisterAccount(request.Email, request.Password, request.Code);
        return Results.Ok(new { username = user.Username, accountExpiresAt = user.ExpiresAt });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/v1/activation/redeem", (ActivationRequest request, HttpContext context) =>
{
    var current = CurrentUser(context, allowExpired: true);
    if (current is null) return Results.Unauthorized();
    try
    {
        var user = store.RedeemActivation(current.Username, current.Hash, request.Code);
        return Results.Ok(new { username = user.Username, accountExpiresAt = user.ExpiresAt, serverTime = DateTimeOffset.UtcNow,
            programExpiries = user.ProgramExpiries });
    }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapGet("/api/v1/programs", (HttpContext context) =>
{
    var user = CurrentUser(context, allowExpired: true);
    if (user is null) return Results.Unauthorized();
    return Results.Ok(store.ListPrograms(user));
});

app.MapDelete("/api/v1/session", (HttpContext context) =>
{
    var token = BearerToken(context);
    if (token is not null)
    {
        sessions.TryRemove(token, out _);
        // Signing out withdraws every run this session authorised; a program started from here
        // stops on its next heartbeat rather than keeping a lease nobody is logged in to.
        launch.RevokeSession(LaunchGate.SessionId(token));
    }
    return Results.NoContent();
});

app.MapPost("/api/v1/programs/{programId}/releases/{version}/launch", (string programId, string version, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!store.CanAccess(user, programId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var publish = store.GetRelease(programId, version);
    if (publish is null) return Results.NotFound();
    // "withdrawn" is still launchable (people who already installed it keep playing);
    // "banned" is not launchable at all. Both produce different codes so a support call can tell
    // which rule stopped the program.
    if (publish.Status == ReleaseStatus.Banned)
        return Results.Json(new { code = "release_banned" }, statusCode: StatusCodes.Status403Forbidden);
    Release? release;
    try
    {
        release = store.LaunchRelease(programId, version);
    }
    catch (InvalidDataException)
    {
        // The bytes on disk are not what the manifest says they are: refuse to hand out launch
        // hashes for a package that no longer matches its signed manifest.
        return Results.Json(new { code = "package_corrupt" }, statusCode: StatusCodes.Status500InternalServerError);
    }
    return release is null ? Results.NotFound() : Results.Ok(release);
});

// What this service can do, so a client can tell "old server" from "no network" instead of
// guessing from a 404 on the endpoint it actually needs.
app.MapGet("/api/v1/launch/capabilities", () => Results.Ok(new
{
    api = launch.Api,
    ticketSeconds = launch.TicketSeconds,
    leaseSeconds = launch.LeaseSeconds,
    heartbeatSeconds = launch.HeartbeatSeconds
}));

// Step 1, from the loader: the account was just authenticated on this request, so the ticket
// only has to say who/what/which version for two minutes and one redemption.
app.MapPost("/api/v1/programs/{programId}/releases/{version}/launch-ticket", (string programId, string version, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!store.CanAccess(user, programId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    // Existence only - the expensive binary hashing stays on the loader's launch path.
    var publish = store.GetRelease(programId, version);
    if (publish is null) return Results.NotFound(new { code = "release_unknown" });
    // A banned version must not be startable even though the loader still sees it installed.
    if (publish.Status == ReleaseStatus.Banned)
        return Results.Json(new { code = "release_banned" }, statusCode: StatusCodes.Status403Forbidden);
    var token = BearerToken(context);
    // The session a lease descends from, so signing out can withdraw it. A request without a
    // bearer token cannot happen here (CurrentUser already required one), but the fallback keeps
    // that guarantee explicit rather than implied.
    var sessionKey = token is null ? Guid.NewGuid().ToString("N") : LaunchGate.SessionId(token);
    var issued = launch.Issue(user, programId, version, sessionKey,
        () => token is not null && sessions.ContainsKey(token));
    return issued is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(issued);
});

// Step 2, from the program itself, with no session: the ticket is the credential, and the
// account, expiry, scope and release are all re-read from the live store here.
app.MapPost("/api/v1/launch/redeem", (RedeemRequest? request, HttpContext context) =>
{
    var (status, body) = launch.Redeem(request, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    return Results.Json(body, statusCode: status);
});

// Step 3: the running program re-earns its lease or is told exactly why it may not continue.
app.MapPost("/api/v1/launch/heartbeat", (HttpContext context) =>
{
    var device = context.Request.Headers["X-Chispa-Device"].ToString();
    var (status, body) = launch.Heartbeat(BearerToken(context), string.IsNullOrWhiteSpace(device) ? null : device);
    return Results.Json(body, statusCode: status);
});

app.MapDelete("/api/v1/launch/lease", (HttpContext context) =>
{
    var (status, _) = launch.Withdraw(BearerToken(context));
    return Results.StatusCode(status);
});

app.MapGet("/api/v1/programs/{programId}/releases/latest", (string programId, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!store.CanAccess(user, programId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var release = store.Latest(programId);
    return release is null ? Results.NotFound(new { error = "No release published" }) : Results.Ok(release);
});

app.MapGet("/api/v1/programs/{programId}/releases/{version}/package", (string programId, string version, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!store.CanAccess(user, programId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var release = store.GetRelease(programId, version);
    if (release is null) return Results.NotFound();
    // Withdrawn and banned versions are not downloadable; already-installed copies are handled by
    // the launch endpoints, which is exactly the difference between the two statuses.
    if (!ReleaseStatus.Downloadable(release.Status))
        return Results.Json(new { code = "release_" + release.Status }, statusCode: StatusCodes.Status404NotFound);
    metrics.Download();
    // Range processing is on so a dropped connection can resume instead of restarting a 90 MB download.
    return Results.File(store.PackagePath(programId, version), "application/zip", $"{programId}-{version}.zip", enableRangeProcessing: true);
});

string? BearerToken(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
}

UserRecord? CurrentUser(HttpContext context, bool allowExpired = false)
{
    var token = BearerToken(context);
    if (token is null) return null;
    if (!sessions.TryGetValue(token, out var session)) return null;
    if (session.ExpiresAt > DateTimeOffset.UtcNow)
    {
        var user = store.CurrentUser(session.Username, session.UserHash, allowExpired);
        if (user is not null) return user;
        // Subscription expiry does not revoke the account's library session.
        if (store.CurrentUser(session.Username, session.UserHash, allowExpired: true) is not null) return null;
    }
    sessions.TryRemove(token, out _);
    return null;
}

var adminGuard = new AdminGuard(dataRoot, app.Logger);
// Optional network restriction for the admin panel (CHISPA_ADMIN_ALLOWED_NETWORKS). Loopback is
// always allowed, so a wrong list can never lock the operator out of the machine the service runs
// on; the recovery path stays the local console and `admin-reset`.
if (adminGuard.AllowedNetworks.Count > 0)
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/admin") &&
            !adminGuard.NetworkAllowed(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden");
            return;
        }
        await next();
    });
    app.Logger.LogInformation("admin panel restricted to {Networks} (loopback always allowed)",
        string.Join(", ", adminGuard.AllowedNetworks.Select(network => network.ToString())));
}
// Bounded cleanup: sessions and failure records are only meaningful until they expire, and the
// dictionaries must not grow with every address that ever knocked. The timer does the pruning off
// the request path; PruneCaches also caps the launch-manifest cache.
var maintenance = new Timer(_ =>
{
    var now = DateTimeOffset.UtcNow;
    foreach (var (key, session) in sessions)
        if (session.ExpiresAt <= now) sessions.TryRemove(key, out var _expired);
    foreach (var (key, failure) in failedLogins)
        if (failure.Until <= now) failedLogins.TryRemove(new KeyValuePair<string, LoginFailures>(key, failure));
    adminGuard.Prune();
    AdminPages.PruneFailedLogins();
    store.PruneCaches();
    limits.Prune();
}, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2));
app.Lifetime.ApplicationStopping.Register(() => maintenance.Dispose());
app.MapAdminPages(store, metrics, () => sessions.Values.Count(
    session => session.ExpiresAt > DateTimeOffset.UtcNow &&
        store.UserAllowed(session.Username, session.UserHash)), adminGuard);
app.MapActivationAdmin(store, adminGuard);
if (Environment.GetEnvironmentVariable("CHISPA_OPEN_ADMIN") == "1")
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo("http://127.0.0.1:5115/admin") { UseShellExecute = true });
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Could not open the admin page automatically."); }
    });
}
app.Run();

static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(chars.ToArray()); }
        if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
    }
}

record Credentials(string Username, string Password);
record RegistrationRequest(string? Email, string? Password, string? Code);
record ActivationRequest(string? Code);
record Session(string Username, string UserHash, DateTimeOffset ExpiresAt);
record LoginFailures(int Count, DateTimeOffset Until);
record UserRecord(string Username, string Salt, string Hash, DateTimeOffset? ExpiresAt = null,
    string AccessMode = "all", string[]? ProgramIds = null, bool BetaAccess = false,
    string? PasswordCiphertext = null, Dictionary<string, DateTimeOffset?>? ProgramExpiries = null);
record UserSummary(string Username, DateTimeOffset? ExpiresAt, string AccessMode, string[] ProgramIds,
    bool BetaAccess, bool PasswordAvailable, Dictionary<string, DateTimeOffset?>? ProgramExpiries = null);
record AdminRecord(string Username, string Salt, string Hash);
record ProgramInfo(string Id, string Name, string Executable, string? Icon = null,
    bool IsBeta = false, string? ParentProgramId = null, bool HasRelease = false,
    // Which version the loader should offer. Null means "the highest active version". Pointing it
    // at an older build is the rollback path after a bad release; pointing it at nothing (or at a
    // version that disappeared) falls back to the highest active one instead of breaking the program.
    string? RecommendedVersion = null, DateTimeOffset? EntitlementExpiresAt = null, bool HasEntitlement = false);
record Release(string ProgramId, string Version, string Executable, string Sha256, long Size,
    Dictionary<string, string>? Binaries = null, string? Signature = null, string? SignatureKeyId = null,
    // "active" (default), "withdrawn" (no longer downloadable, still launchable) or "banned"
    // (neither downloadable nor launchable). Missing in older manifests means "active".
    string Status = "active");
/// <summary>Statuses a release can be in. Strings on purpose: they travel in manifest.json.</summary>
static class ReleaseStatus
{
    public const string Active = "active";
    public const string Withdrawn = "withdrawn";
    public const string Banned = "banned";
    public static bool IsKnown(string status) => status is Active or Withdrawn or Banned;
    public static bool Downloadable(string status) => status == Active;
    public static bool Launchable(string status) => status != Banned;
}

sealed partial class Store
{
    private readonly string root;
    public string UploadDirectory => Path.Combine(root, "uploads");
    private readonly object gate = new();
    private readonly IDataProtector passwordProtector;
    private readonly Dictionary<string, (DateTime Modified, long Size, Release Release)> launchManifests = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex UserName = new("^[a-zA-Z0-9_.-]{3,32}$", RegexOptions.Compiled);
    private static readonly Regex VersionName = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex ProgramId = new("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.Compiled);

    public Store(string root)
    {
        this.root = root;
        Directory.CreateDirectory(root);
        var keys = Path.Combine(root, "keys");
        Directory.CreateDirectory(keys);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), options =>
        {
            options.SetApplicationName("Chispa.Server");
            if (OperatingSystem.IsWindows()) options.ProtectKeysWithDpapi();
        });
        passwordProtector = provider.CreateProtector("Chispa.UserPassword.v1");
        RecoverActivationTransaction();
    }

    public void AddUser(string username, string password, DateTimeOffset? expiresAt = null,
        string accessMode = "all", IEnumerable<string>? programIds = null, bool betaAccess = false)
    {
        if (!UserName.IsMatch(username)) throw new ArgumentException("Username must be 3-32 ASCII letters, digits, _, . or -.");
        if (password.Length < 12) throw new ArgumentException("Password must be at least 12 characters.");
        lock (gate)
        {
            var ids = ValidateAccess(accessMode, programIds);
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            if (users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("User already exists.");
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
            users.Add(new UserRecord(username, Convert.ToBase64String(salt), Convert.ToBase64String(hash),
                expiresAt, accessMode, ids, betaAccess, passwordProtector.Protect(password)));
            WriteAtomic(path, users);
        }
    }

    public UserRecord? AuthenticateUser(string username, string password)
    {
        lock (gate)
        {
            var users = Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [];
            var user = users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (user is null) return null;
            var salt = Convert.FromBase64String(user.Salt);
            var expected = Convert.FromBase64String(user.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected) ? user : null;
        }
    }

    public bool UserAllowed(string username, string userHash)
    {
        return CurrentUser(username, userHash) is not null;
    }

    public UserRecord? CurrentUser(string username, string userHash, bool allowExpired = false)
    {
        lock (gate)
        {
            var users = Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [];
            var user = users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            return user is not null && user.Hash == userHash &&
                (allowExpired || HasActiveSubscription(user)) ? user : null;
        }
    }

    public IReadOnlyList<UserSummary> ListUsers()
    {
        lock (gate)
        {
            return (Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [])
                .Select(u => new UserSummary(u.Username, u.ExpiresAt,
                    u.AccessMode == "beta" ? "all" : u.AccessMode, u.ProgramIds ?? [],
                    u.BetaAccess || u.AccessMode == "beta", u.PasswordCiphertext is not null, Entitlements(u)))
                .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public string? ViewUserPassword(string username)
    {
        lock (gate)
        {
            var users = Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [];
            var user = users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("账号不存在。");
            return user.PasswordCiphertext is null ? null : passwordProtector.Unprotect(user.PasswordCiphertext);
        }
    }

    public void SetUserExpiry(string username, DateTimeOffset? expiresAt)
    {
        lock (gate)
        {
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            var index = users.FindIndex(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ArgumentException("User not found.");
            var user = users[index];
            users[index] = user with { ExpiresAt = expiresAt,
                ProgramExpiries = user.ProgramExpiries?.ToDictionary(p => p.Key, _ => expiresAt) };
            WriteAtomic(path, users);
        }
    }

    public void SetUserPassword(string username, string password)
    {
        if (password.Length < 12) throw new ArgumentException("密码至少 12 个字符。");
        lock (gate)
        {
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            var index = users.FindIndex(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ArgumentException("账号不存在。");
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
            // Replacing the hash also invalidates every live session: sessions carry the
            // hash they signed in with and are rechecked on every request.
            users[index] = users[index] with
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(hash),
                PasswordCiphertext = passwordProtector.Protect(password)
            };
            WriteAtomic(path, users);
        }
    }

    public void SetUserAccess(string username, string accessMode, IEnumerable<string>? programIds, bool betaAccess)
    {
        lock (gate)
        {
            var ids = ValidateAccess(accessMode, programIds);
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            var index = users.FindIndex(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ArgumentException("User not found.");
            var user = users[index];
            if (user.ProgramExpiries is null)
                users[index] = user with { AccessMode = accessMode, ProgramIds = ids, BetaAccess = betaAccess };
            else
            {
                var selected = accessMode == "all" ? ListAllPrograms().Where(p => !p.IsBeta).Select(p => p.Id) : ids;
                var expiries = selected.ToDictionary(id => id,
                    id => user.ProgramExpiries.TryGetValue(id, out var expiry) ? expiry : user.ExpiresAt);
                users[index] = WithEntitlements(user with { BetaAccess = betaAccess }, expiries);
            }
            WriteAtomic(path, users);
        }
    }

    public bool CanAccess(UserRecord user, string programId) => HasProgramScope(user, programId) &&
        (ProgramExpiry(user, programId) is not { } expiry || expiry > DateTimeOffset.UtcNow);

    private bool HasProgramScope(UserRecord user, string programId)
    {
        var program = GetProgram(programId);
        if (program is null) return false;
        if (program.IsBeta && !(user.BetaAccess || user.AccessMode == "beta")) return false;
        var scopeId = program.IsBeta ? program.ParentProgramId ?? program.Id : program.Id;
        if (program.IsBeta && program.ParentProgramId is null) return false;
        if (user.ProgramExpiries is { } expiries) return expiries.ContainsKey(scopeId);
        return user.AccessMode switch
        {
            "beta" => true,
            "single" or "multiple" => (user.ProgramIds ?? []).Contains(scopeId, StringComparer.Ordinal),
            "all" => true,
            _ => false
        };
    }

    private string[] ValidateAccess(string accessMode, IEnumerable<string>? programIds)
    {
        if (accessMode is not ("all" or "single" or "multiple" or "none"))
            throw new ArgumentException("无效的账号分类。");
        var ids = (programIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (accessMode == "none") return [];
        if (accessMode == "single" && ids.Length != 1)
            throw new ArgumentException("单程序账号必须选择一个程序。");
        if (accessMode == "multiple" && ids.Length < 2)
            throw new ArgumentException("多程序账号至少选择两个程序。");
        if (accessMode == "all") return [];
        if (ids.Any(id => GetProgram(id) is not { IsBeta: false }))
            throw new ArgumentException("请选择稳定程序，Beta 权限单独勾选。");
        return ids;
    }

    public void DeleteUser(string username)
    {
        lock (gate)
        {
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            if (users.RemoveAll(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)) == 0)
                throw new ArgumentException("User not found.");
            WriteAtomic(path, users);
        }
    }

    public bool HasAdmin() => File.Exists(Path.Combine(root, "admin.json"));

    public void AddAdmin(string username, string password)
    {
        if (HasAdmin()) throw new InvalidOperationException("An administrator already exists. Use admin-reset locally.");
        SaveAdmin(username, password);
    }

    public void ResetAdmin(string username, string password)
    {
        if (!HasAdmin()) throw new InvalidOperationException("No administrator exists. Use admin-create.");
        SaveAdmin(username, password);
    }

    public bool VerifyAdmin(string username, string password)
    {
        lock (gate)
        {
            var admin = Read<AdminRecord>(Path.Combine(root, "admin.json"));
            if (admin is null || !admin.Username.Equals(username, StringComparison.OrdinalIgnoreCase)) return false;
            var expected = Convert.FromBase64String(admin.Hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(admin.Salt),
                310_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
    }

    public string? AdminHash(string username)
    {
        lock (gate)
        {
            var admin = Read<AdminRecord>(Path.Combine(root, "admin.json"));
            return admin?.Username.Equals(username, StringComparison.OrdinalIgnoreCase) == true ? admin.Hash : null;
        }
    }

    public bool AdminSessionValid(string? username, string? hash) =>
        username is not null && hash is not null && AdminHash(username) == hash;

    private void SaveAdmin(string username, string password)
    {
        if (!UserName.IsMatch(username)) throw new ArgumentException("Admin name must be 3-32 ASCII letters, digits, _, . or -.");
        if (password.Length < 12) throw new ArgumentException("Admin password must be at least 12 characters.");
        lock (gate)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
            WriteAtomic(Path.Combine(root, "admin.json"),
                new AdminRecord(username, Convert.ToBase64String(salt), Convert.ToBase64String(hash)));
        }
    }

    public void AddProgram(string id, string name, string executable, bool isBeta = false,
        string? icon = null, string? parentProgramId = null)
    {
        if (!ProgramId.IsMatch(id)) throw new ArgumentException("Program ID must be 2-32 lowercase letters, digits or hyphens.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Display name must be 1-80 characters.");
        if (!ValidExecutable(executable)) throw new ArgumentException("Executable must be a simple .exe filename.");
        ValidateIcon(icon);
        ValidateParent(id, isBeta, parentProgramId);
        lock (gate)
        {
            var path = Path.Combine(root, "programs", id, "program.json");
            var current = Read<ProgramInfo>(path);
            var program = new ProgramInfo(id, name.Trim(), executable, icon, isBeta, parentProgramId);
            if (current is not null)
            {
                if (current.Executable != executable)
                    throw new InvalidOperationException("Program ID already exists with a different executable.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomic(path, program);
        }
    }

    public void UpdateProgram(string id, string name, string? icon, bool isBeta, string? parentProgramId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new ArgumentException("显示名称长度应为 1 至 80 个字符。");
        ValidateIcon(icon);
        ValidateParent(id, isBeta, parentProgramId);
        lock (gate)
        {
            var path = Path.Combine(root, "programs", id, "program.json");
            var current = GetProgram(id) ?? throw new ArgumentException("程序不存在。");
            WriteAtomic(path, current with
            {
                Name = name.Trim(), Icon = icon, IsBeta = isBeta,
                ParentProgramId = isBeta ? parentProgramId : null
            });
        }
    }

    public ProgramInfo? FindProgram(string id) => GetProgram(id);

    public void DeleteProgram(string id)
    {
        if (!ProgramId.IsMatch(id)) throw new ArgumentException("程序 ID 无效。");
        lock (gate)
        {
            var directory = Path.Combine(root, "programs", id);
            if (!File.Exists(Path.Combine(directory, "program.json"))) throw new ArgumentException("程序不存在。");

            // Clear authorisation references first so no account keeps a dead program ID.
            var usersPath = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(usersPath) ?? [];
            var usersChanged = false;
            for (var index = 0; index < users.Count; index++)
            {
                var user = users[index];
                var ids = user.ProgramIds ?? [];
                if (!ids.Contains(id, StringComparer.Ordinal) && user.ProgramExpiries?.ContainsKey(id) != true) continue;
                if (user.ProgramExpiries is not null)
                {
                    var expiries = new Dictionary<string, DateTimeOffset?>(user.ProgramExpiries);
                    expiries.Remove(id);
                    users[index] = WithEntitlements(user, expiries);
                    usersChanged = true;
                    continue;
                }
                var kept = ids.Where(value => !value.Equals(id, StringComparison.Ordinal)).ToArray();
                var mode = user.AccessMode;
                if (mode == "multiple" && kept.Length == 1) mode = "single";
                if (mode is "single" or "multiple" && kept.Length == 0) mode = "none";
                users[index] = user with { AccessMode = mode, ProgramIds = kept };
                usersChanged = true;
            }
            if (usersChanged) WriteAtomic(usersPath, users);

            // Beta options lose the stable program they were attached to.
            foreach (var child in ListAllPrograms().Where(p => p.IsBeta && p.ParentProgramId == id))
                WriteAtomic(Path.Combine(root, "programs", child.Id, "program.json"), child with { ParentProgramId = null });

            foreach (var key in launchManifests.Keys.Where(key => key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray())
                launchManifests.Remove(key);

            Directory.Delete(directory, recursive: true);
        }
    }

    public IReadOnlyList<ProgramInfo> ListPrograms(UserRecord? user = null)
    {
        var path = Path.Combine(root, "programs");
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateDirectories(path)
            .Select(d => Path.GetFileName(d))
            .Where(id => ProgramId.IsMatch(id))
            .Select(GetProgram)
            .Where(p => p is not null && (user is null || HasProgramScope(user, p.Id)))
            .Cast<ProgramInfo>()
            .Select(p => p with { HasRelease = Latest(p.Id) is not null,
                HasEntitlement = user is not null, EntitlementExpiresAt = user is null ? null : ProgramExpiry(user, p.Id) })
            .OrderByDescending(p => p.HasRelease)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<ProgramInfo> ListAllPrograms()
    {
        var path = Path.Combine(root, "programs");
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateDirectories(path)
            .Select(d => Path.GetFileName(d))
            .Where(id => ProgramId.IsMatch(id))
            .Select(GetProgram)
            .OfType<ProgramInfo>()
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ---- publishing --------------------------------------------------------------------------
    // A release is only stored together with a signature that verifies against the configured
    // trust keys. This process has no private key and no way to obtain one, so a compromised
    // download service cannot manufacture a release the loader will accept: all it can do is
    // refuse to serve, or serve an older package that the release environment signed.

    /// <summary>Public keys the server checks uploads against. Separate from the keys compiled
    /// into the loader (which are the actual trust anchors): this one only exists to catch a broken
    /// signing step before it is distributed.</summary>
    public IReadOnlyList<TrustedReleaseKey> TrustKeys()
    {
        var configured = Environment.GetEnvironmentVariable("CHISPA_RELEASE_TRUST_KEYS");
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(root, "release-trust.json") : configured;
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"发布签名校验没有配置：找不到公钥文件 {path}。用 chispa-sign keygen 生成密钥，把公钥文件放到这里，或设置 CHISPA_RELEASE_TRUST_KEYS 指向它。");
        try { return ReleaseTrustFile.Load(path); }
        catch (InvalidDataException ex) { throw new InvalidOperationException("公钥文件无效：" + ex.Message); }
    }

    public Task<Release> PublishArchiveAsync(string programId, string version, Stream source, string signatureJson,
        CancellationToken cancellationToken) =>
        PublishCoreAsync(programId, version, signatureJson, async package =>
        {
            await using var destination = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }, cancellationToken);

    public Task<Release> PublishArchiveFileAsync(string programId, string version, string uploadPath,
        string signatureJson, CancellationToken cancellationToken) =>
        PublishCoreAsync(programId, version, signatureJson, package =>
        {
            // Uploads live under the same data root, so this move avoids a second
            // full-size disk copy before validating and publishing the archive.
            File.Move(uploadPath, package);
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>CLI entry: publish a package file that was produced and signed elsewhere.</summary>
    public Release PublishPackage(string programId, string version, string packagePath, string signaturePath)
    {
        if (!File.Exists(packagePath)) throw new ArgumentException($"包不存在：{packagePath}");
        if (!File.Exists(signaturePath)) throw new ArgumentException($"签名文件不存在：{signaturePath}");
        using var stream = File.OpenRead(packagePath);
        return PublishArchiveAsync(programId, version, stream, File.ReadAllText(signaturePath),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task<Release> PublishCoreAsync(string programId, string version, string signatureJson,
        Func<string, Task> stagePackage, CancellationToken cancellationToken)
    {
        var program = GetProgram(programId) ?? throw new ArgumentException("请先登记程序。");
        if (!VersionName.IsMatch(version) || !Version.TryParse(version, out _))
            throw new ArgumentException("版本号格式应为 1.0.0。");
        var releaseRoot = Path.Combine(root, "programs", programId, "releases");
        Directory.CreateDirectory(releaseRoot);
        var final = Path.Combine(releaseRoot, version);
        if (Directory.Exists(final)) throw new InvalidOperationException("这个版本已经发布。");
        var stage = Path.Combine(releaseRoot, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var package = Path.Combine(stage, "package.zip");
            await stagePackage(package);
            cancellationToken.ThrowIfCancellationRequested();
            var verified = SignedPublish.Verify(programId, version, program.Executable, package,
                signatureJson, TrustKeys());
            var release = new Release(programId, version, program.Executable, verified.Sha256, verified.Size,
                verified.Binaries, SignatureKeyId: verified.KeyId);
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(release, Json));
            // The envelope is stored verbatim and separately: it is the artifact that gets verified,
            // so keeping the exact bytes (rather than a re-serialisation of them) matters.
            File.WriteAllText(Path.Combine(stage, "signature.json"), signatureJson);
            lock (gate)
            {
                if (Directory.Exists(final)) throw new InvalidOperationException("这个版本已经发布。");
                Directory.Move(stage, final);
            }
            return release;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    /// <summary>Attaches a signature to a release that was published before signatures existed.
    /// Same verification as a fresh publish - the package has to be the one that was signed - and
    /// it also records the per-file hashes the signature binds, which old manifests never had.</summary>
    public Release SignExistingRelease(string programId, string version, string signatureJson)
    {
        var program = GetProgram(programId) ?? throw new ArgumentException("程序不存在。");
        var release = GetRelease(programId, version) ?? throw new ArgumentException("这个版本不存在，或包已损坏。");
        var verified = SignedPublish.Verify(programId, version, program.Executable,
            PackagePath(programId, version), signatureJson, TrustKeys());
        lock (gate)
        {
            var directory = Path.Combine(root, "programs", programId, "releases", version);
            File.WriteAllText(Path.Combine(directory, "signature.json"), signatureJson);
            var manifest = release with
            {
                Binaries = verified.Binaries,
                SignatureKeyId = verified.KeyId,
                Signature = null,
            };
            WriteAtomic(Path.Combine(directory, "manifest.json"), manifest);
            return manifest with { Signature = Convert.ToBase64String(Encoding.UTF8.GetBytes(signatureJson)) };
        }
    }

    // What the loader should offer. The recommended pointer wins when it still names an active
    // release; otherwise the highest active version does. Withdrawn and banned versions are never
    // "latest": withdrawn ones stay launchable for people who already have them, banned ones do not.
    public Release? Latest(string programId)
    {
        var program = GetProgram(programId);
        if (program is null || !ProgramId.IsMatch(programId)) return null;
        var path = Path.Combine(root, "programs", programId, "releases");
        if (!Directory.Exists(path)) return null;
        if (program.RecommendedVersion is { Length: > 0 } recommended)
        {
            var release = GetRelease(programId, recommended);
            // A dangling recommendation (version withdrawn, directory deleted, typo) must not break
            // the program: fall back to the highest active version instead of returning nothing.
            if (release is not null && ReleaseStatus.Downloadable(release.Status)) return release;
        }
        return Directory.EnumerateDirectories(path)
            .Select(d => Path.GetFileName(d))
            .Where(v => VersionName.IsMatch(v) && Version.TryParse(v, out _))
            .OrderByDescending(Version.Parse)
            .Select(version => GetRelease(programId, version))
            .FirstOrDefault(release => release is not null && ReleaseStatus.Downloadable(release.Status));
    }

    /// <summary>Every version of a program, newest first, whatever its status. Used by the admin
    /// page; the loader only ever sees <see cref="Latest"/>.</summary>
    public IReadOnlyList<Release> Releases(string programId)
    {
        if (!ProgramId.IsMatch(programId) || GetProgram(programId) is null) return [];
        var path = Path.Combine(root, "programs", programId, "releases");
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateDirectories(path)
            .Select(d => Path.GetFileName(d))
            .Where(v => VersionName.IsMatch(v) && Version.TryParse(v, out _))
            .OrderByDescending(Version.Parse)
            .Select(version => GetRelease(programId, version))
            .Where(release => release is not null)
            .Select(release => release!)
            .ToArray();
    }

    public Release SetReleaseStatus(string programId, string version, string status)
    {
        if (!ReleaseStatus.IsKnown(status)) throw new ArgumentException("版本状态无效。");
        lock (gate)
        {
            var release = GetRelease(programId, version) ?? throw new ArgumentException("这个版本不存在。");
            var directory = Path.Combine(root, "programs", programId, "releases", version);
            var manifest = release with { Signature = null, Status = status };
            WriteAtomic(Path.Combine(directory, "manifest.json"), manifest);
            return release with { Status = status };
        }
    }

    public ProgramInfo SetRecommendedVersion(string programId, string? version)
    {
        lock (gate)
        {
            var program = GetProgram(programId) ?? throw new ArgumentException("程序不存在。");
            if (!string.IsNullOrEmpty(version))
            {
                var release = GetRelease(programId, version) ?? throw new ArgumentException("这个版本不存在，无法设为推荐版本。");
                if (!ReleaseStatus.Downloadable(release.Status))
                    throw new ArgumentException("撤回或禁止的版本不能设为推荐版本。");
            }
            var updated = program with { RecommendedVersion = string.IsNullOrEmpty(version) ? null : version };
            WriteAtomic(Path.Combine(root, "programs", programId, "program.json"), updated);
            return updated;
        }
    }

    public Release? GetRelease(string programId, string version)
    {
        if (!ProgramId.IsMatch(programId) || !VersionName.IsMatch(version)) return null;
        var program = GetProgram(programId);
        if (program is null) return null;
        var releaseDirectory = Path.Combine(root, "programs", programId, "releases", version);
        var path = Path.Combine(releaseDirectory, "manifest.json");
        var package = PackagePath(programId, version);
        var release = File.Exists(path) && File.Exists(package) ? Read<Release>(path) : null;
        if (release?.ProgramId != programId || release.Version != version ||
            release.Executable != program.Executable)
            return null;
        // The envelope lives beside the package, not inside the manifest, so that (a) it is stored
        // as the exact bytes that were signed and (b) attaching one to an already-published release
        // is a file drop rather than a manifest rewrite. A release without one is served unsigned,
        // and the loader refuses to install it - which is the intended state for old packages that
        // have not been migrated yet.
        var signaturePath = Path.Combine(releaseDirectory, "signature.json");
        try
        {
            if (!File.Exists(signaturePath)) return release;
            return release with { Signature = Convert.ToBase64String(File.ReadAllBytes(signaturePath)) };
        }
        catch (IOException) { return release; }
    }

    /// <summary>Caps the launch-manifest cache. Releases are immutable, so entries can be dropped at
    /// any time; they are re-derived on the next launch.</summary>
    public void PruneCaches()
    {
        lock (gate)
        {
            if (launchManifests.Count <= 128) return;
            foreach (var key in launchManifests.Keys.Take(launchManifests.Count - 128).ToArray())
                launchManifests.Remove(key);
        }
    }
    public Release? LaunchRelease(string programId, string version)
    {
        var release = GetRelease(programId, version);
        if (release is null) return null;
        var package = PackagePath(programId, version);
        var info = new FileInfo(package);
        var key = programId + "/" + version + "/" + release.Sha256;
        // Fast path under the lock: a verified manifest is an immutable release, so a cached result
        // is returned without touching the disk again.
        lock (gate)
        {
            if (launchManifests.TryGetValue(key, out var cached) &&
                cached.Modified == info.LastWriteTimeUtc && cached.Size == info.Length)
                return release with { Binaries = cached.Release.Binaries };
        }
        // Hashing a 90 MB package is cheap but not free, and it used to happen while holding the
        // store-wide lock - which blocked every other request behind it. A release directory is
        // never rewritten in place (only manifest.json changes, and that is not part of the digest),
        // so the work is safe to do outside the lock. Two requests racing on the same release both
        // hash it; the second one wins the cache write, and the answer is identical either way.
        Dictionary<string, string> binaries;
        using (var stream = File.OpenRead(package))
        {
            if (stream.Length != release.Size ||
                !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Published package does not match its manifest.");
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            binaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (Path.GetExtension(entry.FullName).ToLowerInvariant() is not (".exe" or ".dll" or ".sys" or ".ocx" or ".com")) continue;
                using var contents = entry.Open();
                binaries.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(contents)));
            }
        }
        var verified = release with { Binaries = binaries };
        lock (gate) launchManifests[key] = (info.LastWriteTimeUtc, info.Length, verified);
        return verified;
    }

    public string PackagePath(string programId, string version)
    {
        if (!ProgramId.IsMatch(programId) || !VersionName.IsMatch(version)) throw new ArgumentException("Invalid program or version.");
        return Path.Combine(root, "programs", programId, "releases", version, "package.zip");
    }

    private ProgramInfo? GetProgram(string id)
    {
        if (!ProgramId.IsMatch(id)) return null;
        var path = Path.Combine(root, "programs", id, "program.json");
        var program = Read<ProgramInfo>(path);
        return program?.Id == id && ValidExecutable(program.Executable) ? program : null;
    }

    private static bool ValidExecutable(string executable) =>
        !string.IsNullOrWhiteSpace(executable) && executable.Length <= 120 &&
        executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        executable == Path.GetFileName(executable) &&
        executable.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void ValidateIcon(string? icon)
    {
        if (icon is null) return;
        if (icon.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
        {
            if (icon.Length > 180_000) throw new ArgumentException("图标文件过大。");
        }
        else if (icon.Length > 16 || icon.Any(char.IsControl))
            throw new ArgumentException("文字图标最多 16 个字符。");
    }

    private void ValidateParent(string id, bool isBeta, string? parentProgramId)
    {
        if (!isBeta)
        {
            if (parentProgramId is not null)
                throw new ArgumentException("稳定程序不能设置 Beta 所属程序。");
            return;
        }
        if (parentProgramId is null || parentProgramId == id ||
            GetProgram(parentProgramId) is not { IsBeta: false })
            throw new ArgumentException("Beta 选项必须关联一个已登记的稳定程序。");
    }

    private T? Read<T>(string path)
    {
        lock (gate)
        {
            RecoverActivationTransaction();
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
    }

    private static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
