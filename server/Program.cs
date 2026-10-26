using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;

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
            case "publish" when args.Length == 4:
                var release = store.Publish(args[1], args[2], args[3]);
                Console.WriteLine($"Published {release.ProgramId} {release.Version} ({release.Size} bytes, SHA-256 {release.Sha256}).");
                return;
            default:
                Console.Error.WriteLine("Usage: Chispa.Server [admin-create <username> | admin-reset <username> | user-add <username> | program-add <id> <display-name> <executable.exe> [beta <parent-id>] | program-link-beta <id> <parent-id> | publish <id> <version> <prepared-runtime-folder>]");
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
builder.WebHost.ConfigureKestrel(options =>
{
    // Release packages are uploaded straight from the admin page. Keep the body limit
    // aligned with the form limit and stop Kestrel's minimum data rate from aborting
    // slow uploads of large ZIP files.
    options.Limits.MaxRequestBodySize = 1024L * 1024 * 1024 + 8 * 1024 * 1024;
    options.Limits.MinRequestBodyDataRate = null;
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 1024L * 1024 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
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
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.HeaderName = "X-CSRF-TOKEN";
});
var app = builder.Build();
var sessions = new ConcurrentDictionary<string, Session>();
var failedLogins = new ConcurrentDictionary<string, LoginFailures>();
var metrics = new ServerMetrics();

// Remote deployment must terminate TLS at this service or a trusted reverse proxy.
app.Use(async (context, next) =>
{
    if (!context.Request.IsHttps &&
        !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? System.Net.IPAddress.None))
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
    return Results.Ok(new { token, expiresAt = expires });
});

app.MapGet("/api/v1/programs", (HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    return Results.Ok(store.ListPrograms(user));
});

app.MapDelete("/api/v1/session", (HttpContext context) =>
{
    var header = context.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) sessions.TryRemove(header[7..].Trim(), out _);
    return Results.NoContent();
});

app.MapPost("/api/v1/programs/{programId}/releases/{version}/launch", (string programId, string version, HttpContext context) =>
{
    var user = CurrentUser(context);
    if (user is null) return Results.Unauthorized();
    if (!store.CanAccess(user, programId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var release = store.LaunchRelease(programId, version);
    return release is null ? Results.NotFound() : Results.Ok(release);
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
    metrics.Download();
    return Results.File(store.PackagePath(programId, version), "application/zip", $"{programId}-{version}.zip");
});

UserRecord? CurrentUser(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
    var token = header[7..].Trim();
    if (!sessions.TryGetValue(token, out var session)) return null;
    if (session.ExpiresAt > DateTimeOffset.UtcNow)
    {
        var user = store.CurrentUser(session.Username, session.UserHash);
        if (user is not null) return user;
    }
    sessions.TryRemove(token, out _);
    return null;
}

app.MapAdminPages(store, metrics, () => sessions.Values.Count(
    session => session.ExpiresAt > DateTimeOffset.UtcNow &&
        store.UserAllowed(session.Username, session.UserHash)));
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
record Session(string Username, string UserHash, DateTimeOffset ExpiresAt);
record LoginFailures(int Count, DateTimeOffset Until);
record UserRecord(string Username, string Salt, string Hash, DateTimeOffset? ExpiresAt = null,
    string AccessMode = "all", string[]? ProgramIds = null, bool BetaAccess = false,
    string? PasswordCiphertext = null);
record UserSummary(string Username, DateTimeOffset? ExpiresAt, string AccessMode, string[] ProgramIds,
    bool BetaAccess, bool PasswordAvailable);
record AdminRecord(string Username, string Salt, string Hash);
record ProgramInfo(string Id, string Name, string Executable, string? Icon = null,
    bool IsBeta = false, string? ParentProgramId = null, bool HasRelease = false);
record Release(string ProgramId, string Version, string Executable, string Sha256, long Size,
    Dictionary<string, string>? Binaries = null);

sealed class Store
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
            return CryptographicOperations.FixedTimeEquals(actual, expected) &&
                (user.ExpiresAt is null || user.ExpiresAt > DateTimeOffset.UtcNow) ? user : null;
        }
    }

    public bool UserAllowed(string username, string userHash)
    {
        return CurrentUser(username, userHash) is not null;
    }

    public UserRecord? CurrentUser(string username, string userHash)
    {
        lock (gate)
        {
            var users = Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [];
            var user = users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            return user is not null && user.Hash == userHash &&
                (user.ExpiresAt is null || user.ExpiresAt > DateTimeOffset.UtcNow) ? user : null;
        }
    }

    public IReadOnlyList<UserSummary> ListUsers()
    {
        lock (gate)
        {
            return (Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [])
                .Select(u => new UserSummary(u.Username, u.ExpiresAt,
                    u.AccessMode == "beta" ? "all" : u.AccessMode, u.ProgramIds ?? [],
                    u.BetaAccess || u.AccessMode == "beta", u.PasswordCiphertext is not null))
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
            users[index] = users[index] with { ExpiresAt = expiresAt };
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
            users[index] = users[index] with { AccessMode = accessMode, ProgramIds = ids, BetaAccess = betaAccess };
            WriteAtomic(path, users);
        }
    }

    public bool CanAccess(UserRecord user, string programId)
    {
        var program = GetProgram(programId);
        if (program is null) return false;
        if (program.IsBeta && !(user.BetaAccess || user.AccessMode == "beta")) return false;
        var scopeId = program.IsBeta ? program.ParentProgramId ?? program.Id : program.Id;
        return user.AccessMode switch
        {
            "beta" => true,
            "single" or "multiple" => (user.ProgramIds ?? []).Contains(scopeId, StringComparer.Ordinal) ||
                (user.ProgramIds ?? []).Contains(programId, StringComparer.Ordinal),
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
                if (!ids.Contains(id, StringComparer.Ordinal)) continue;
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
            .Where(p => p is not null && (user is null || CanAccess(user, p.Id)))
            .Cast<ProgramInfo>()
            .Select(p => p with { HasRelease = Latest(p.Id) is not null })
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

    public Task<Release> PublishArchiveAsync(string programId, string version, Stream source, CancellationToken cancellationToken) =>
        PublishArchiveCoreAsync(programId, version, async package =>
        {
            await using var destination = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
        }, cancellationToken);

    public Task<Release> PublishArchiveFileAsync(string programId, string version, string uploadPath,
        CancellationToken cancellationToken) =>
        PublishArchiveCoreAsync(programId, version, package =>
        {
            // Uploads live under the same data root, so this move avoids a second
            // full-size disk copy before validating and publishing the archive.
            File.Move(uploadPath, package);
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task<Release> PublishArchiveCoreAsync(string programId, string version,
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
            NormalizePackage(package, program.Executable);
            string sha;
            long size;
            using (var stream = File.OpenRead(package))
            {
                sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                size = stream.Length;
            }
            var release = new Release(programId, version, program.Executable, sha, size);
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(release, Json));
            lock (gate)
            {
                if (Directory.Exists(final)) throw new InvalidOperationException("这个版本已经发布。");
                Directory.Move(stage, final);
            }
            return release;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    // Windows Explorer's "compress to ZIP" wraps everything in the selected folder, macOS
    // adds __MACOSX entries and some tools write backslashes. Normalise those packs so the
    // entry EXE really sits at the ZIP root, which is what the client extracts and verifies.
    private static void NormalizePackage(string path, string executable)
    {
        var replacement = path + ".normalized";
        try
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                if (archive.Entries.Count > 20_000)
                    throw new ArgumentException("ZIP 内的文件数量过多（上限 20000 个）。");
                var files = new List<(ZipArchiveEntry Entry, string Name)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                var droppedJunk = false;
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0) continue; // directory entry
                    var name = NormalizeEntryName(entry.FullName);
                    if (IsJunkEntry(name)) { droppedJunk = true; continue; }
                    if (!seen.Add(name)) throw new ArgumentException($"ZIP 包含重复的文件路径：{name}");
                    expanded += entry.Length;
                    if (expanded > 4L * 1024 * 1024 * 1024)
                        throw new ArgumentException("ZIP 解压后的内容过大（上限 4 GB）。");
                    files.Add((entry, name));
                }
                if (files.Count == 0) throw new ArgumentException("ZIP 里没有文件，请重新打包。");

                var wrapper = CommonWrapper(files.Select(file => file.Name));
                var final = files
                    .Select(file => wrapper is null ? file.Name : file.Name[(wrapper.Length + 1)..])
                    .ToArray();
                var renamed = droppedJunk || files.Where((file, index) => final[index] != file.Entry.FullName).Any();
                if (!final.Any(name => name.Equals(executable, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"ZIP 顶层必须包含 {executable}。" + TopLevelHint(final));

                if (!renamed) return;
                using (var output = ZipFile.Open(replacement, ZipArchiveMode.Create))
                {
                    for (var index = 0; index < files.Count; index++)
                    {
                        var source = files[index].Entry;
                        var copy = output.CreateEntry(final[index], CompressionLevel.Optimal);
                        copy.LastWriteTime = source.LastWriteTime;
                        using var input = source.Open();
                        using var destination = copy.Open();
                        input.CopyTo(destination);
                    }
                }
            }
            File.Move(replacement, path, overwrite: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("ZIP 文件无法读取或格式无效。");
        }
        finally
        {
            if (File.Exists(replacement)) File.Delete(replacement);
        }
    }

    private static string NormalizeEntryName(string raw)
    {
        var name = raw.Replace('\\', '/').Trim();
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0 || name.Length > 512 || name.StartsWith('/') || name.Contains(':') ||
            name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw new ArgumentException("ZIP 包含不安全或无效的文件路径。");
        return name;
    }

    private static bool IsJunkEntry(string name) =>
        name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase);

    // A single top-level folder wrapping the whole package is stripped automatically.
    private static string? CommonWrapper(IEnumerable<string> names)
    {
        string? prefix = null;
        foreach (var name in names)
        {
            var slash = name.IndexOf('/');
            if (slash <= 0) return null;
            var top = name[..slash];
            if (prefix is null) prefix = top;
            else if (!prefix.Equals(top, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return prefix;
    }

    private static string TopLevelHint(IReadOnlyList<string> names)
    {
        var tops = names.Select(name => name.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
        return tops.Length == 0
            ? ""
            : $"当前 ZIP 顶层是：{string.Join("、", tops)}。请把入口 EXE 和全部运行文件直接放进 ZIP 顶层，或只放进一个外层文件夹里。";
    }

    public Release Publish(string programId, string version, string source)
    {
        var program = GetProgram(programId) ?? throw new ArgumentException("Register the program before publishing.");
        if (!VersionName.IsMatch(version) || !Version.TryParse(version, out _))
            throw new ArgumentException("Version must be numeric major.minor.patch, e.g. 1.0.0.");
        source = Path.GetFullPath(source);
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, program.Executable)))
            throw new ArgumentException($"Prepared runtime folder must contain {program.Executable}.");
        var releaseRoot = Path.Combine(root, "programs", programId, "releases");
        Directory.CreateDirectory(releaseRoot);
        var final = Path.Combine(releaseRoot, version);
        if (Directory.Exists(final)) throw new InvalidOperationException("Release version already exists.");
        var stage = Path.Combine(releaseRoot, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var zip = Path.Combine(stage, "package.zip");
            ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            string sha;
            long size;
            using (var stream = File.OpenRead(zip))
            {
                sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                size = stream.Length;
            }
            var release = new Release(programId, version, program.Executable, sha, size);
            File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(release, Json));
            Directory.Move(stage, final);
            return release;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    public Release? Latest(string programId)
    {
        if (!ProgramId.IsMatch(programId) || GetProgram(programId) is null) return null;
        var path = Path.Combine(root, "programs", programId, "releases");
        if (!Directory.Exists(path)) return null;
        return Directory.EnumerateDirectories(path)
            .Select(d => Path.GetFileName(d))
            .Where(v => VersionName.IsMatch(v) && Version.TryParse(v, out _))
            .OrderByDescending(Version.Parse)
            .Select(version => GetRelease(programId, version)).FirstOrDefault(r => r is not null);
    }

    public Release? GetRelease(string programId, string version)
    {
        if (!ProgramId.IsMatch(programId) || !VersionName.IsMatch(version)) return null;
        var program = GetProgram(programId);
        if (program is null) return null;
        var path = Path.Combine(root, "programs", programId, "releases", version, "manifest.json");
        var package = PackagePath(programId, version);
        var release = File.Exists(path) && File.Exists(package) ? Read<Release>(path) : null;
        return release?.ProgramId == programId && release.Version == version && release.Executable == program.Executable ? release : null;
    }

    public Release? LaunchRelease(string programId, string version)
    {
        var release = GetRelease(programId, version);
        if (release is null) return null;
        var package = PackagePath(programId, version);
        var info = new FileInfo(package);
        var key = programId + "/" + version + "/" + release.Sha256;
        lock (gate)
        {
            if (launchManifests.TryGetValue(key, out var cached) && cached.Modified == info.LastWriteTimeUtc && cached.Size == info.Length)
                return cached.Release;
            // Also supports releases published before launch verification existed.
            using var stream = File.OpenRead(package);
            if (stream.Length != release.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Published package does not match its manifest.");
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var binaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (Path.GetExtension(entry.FullName).ToLowerInvariant() is not (".exe" or ".dll" or ".sys" or ".ocx" or ".com")) continue;
                using var contents = entry.Open();
                binaries.Add(entry.FullName, Convert.ToHexString(SHA256.HashData(contents)));
            }
            var verified = release with { Binaries = binaries };
            launchManifests[key] = (info.LastWriteTimeUtc, info.Length, verified);
            return verified;
        }
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

    private static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;

    private static void WriteAtomic<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
