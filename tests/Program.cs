using Chispa.Launcher;
using Chispa.Release;
using System.Reflection;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

// Test-only trust anchors. The shipped loader verifies against the keys compiled into
// client/ReleaseTrust.cs; a test that published with a throwaway key could not use those. This
// variable is read HERE, in the test entry point, and never by the shipped program.
var trustKeys = Environment.GetEnvironmentVariable("CHISPA_TRUST_KEYS") is { Length: > 0 } trustKeyPath
    ? ReleaseTrustFile.Load(trustKeyPath)
    : ReleaseTrust.Keys;

// White-box check of the client-side rules against a manifest supplied as a file. The end-to-end
// scenarios drive tampering through the server; this one feeds the installer manifests it would
// never receive from a working server (a valid signature over a different program, a substituted
// file map), which is exactly the state a compromised download service would try to produce.
if (args.Length == 3 && args[0] == "--verify-release")
{
    // <trustKeys.json> <release.json>
    var keys = ReleaseTrustFile.Load(args[1]);
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var release = JsonSerializer.Deserialize<Release>(File.ReadAllText(args[2]), options)
        ?? throw new Exception("release json did not parse");
    try
    {
        var verified = new Installer(keys).VerifySignature(release);
        if (release.Binaries is { Count: > 0 } map) ReleaseSignature.CompareBinaries(verified, map);
        Console.WriteLine("VERIFY OK " + release.ProgramId + " " + release.Version);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("VERIFY REFUSED " + ex.Message);
        return 3;
    }
}


// Signature acceptance matrix driver: publish tampered or unsigned state on the server, then ask
// the real installer what it thinks. Two entry points, because instalment and launch verify
// different things (the package signature vs. the signed per-file map).
if (args.Length == 6 && args[0] == "--install-check")
{
    // <server> <programId> <version> <account> <password>
    using var checkApi = new LauncherApi(args[1]);
    var checkInstaller = new Installer(trustKeys);
    try
    {
        await checkApi.LoginAsync(args[4], args[5], CancellationToken.None);
        var catalog = await checkApi.GetProgramsAsync(CancellationToken.None);
        var program = catalog.FirstOrDefault(entry => entry.Id == args[2])
            ?? throw new InvalidOperationException("program not in catalog");
        var latest = await checkApi.GetLatestAsync(args[2], CancellationToken.None);
        if (latest.Version != args[3]) throw new InvalidOperationException($"latest is {latest.Version}, expected {args[3]}");
        await checkInstaller.InstallAsync(checkApi, program, latest, null, CancellationToken.None);
        Console.WriteLine("INSTALL OK " + latest.Version);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("INSTALL REFUSED " + ex.Message);
        return 3;
    }
}

if (args.Length == 6 && args[0] == "--download-check")
{
    // <server> <programId> <account> <password> <destination>
    // Exercises the loader's real download path (bounded retry + Range resume + size check).
    using var checkApi = new LauncherApi(args[1]);
    try
    {
        await checkApi.LoginAsync(args[3], args[4], CancellationToken.None);
        var release = await checkApi.GetLatestAsync(args[2], CancellationToken.None);
        await checkApi.DownloadAsync(release, args[5], null, CancellationToken.None);
        var length = new FileInfo(args[5]).Length;
        Console.WriteLine($"DOWNLOAD OK {release.Version} {length}");
        return length == release.Size ? 0 : 4;
    }
    catch (Exception ex)
    {
        Console.WriteLine("DOWNLOAD FAILED " + ex.GetType().Name + ": " + ex.Message);
        return 3;
    }
}

if (args.Length == 5 && args[0] == "--launch-check")
{
    // <server> <programId> <account> <password>
    using var checkApi = new LauncherApi(args[1]);
    var checkInstaller = new Installer(trustKeys);
    try
    {
        await checkApi.LoginAsync(args[3], args[4], CancellationToken.None);
        var catalog = await checkApi.GetProgramsAsync(CancellationToken.None);
        var program = catalog.FirstOrDefault(entry => entry.Id == args[2])
            ?? throw new InvalidOperationException("program not in catalog");
        var directory = await checkInstaller.VerifyInstalledAsync(checkApi, program, CancellationToken.None);
        Console.WriteLine("LAUNCH OK " + directory);
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("LAUNCH REFUSED " + ex.Message);
        return 3;
    }
}

// Cross-language check for the launch hand-off. The loader writes the ticket file in C# and the
// program reads it in C++, so the fragile part is the interface itself: DPAPI scope and
// entropy, the JSON field names and the timestamp format. Writing the file with the loader's own
// code is the only way to test the reader against it instead of against a copy of the format.
if (args.Length is 7 or 8 && args[0] == "--write-launch-ticket")
{
    // <programId> <server> <version> <ticket> <expiresIso> <authDirectory>
    Environment.SetEnvironmentVariable("CHISPA_AUTH_DIR", args[6]);
    var expires = DateTimeOffset.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind);
    var path = LaunchTicketFile.Write(args[1], args[2], args[3],
        new LaunchTicket(args[4], expires, DateTimeOffset.UtcNow, 120), CancellationToken.None,
        uniqueName: args.Length == 8 && args[7] == "unique");
    if (System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(args[4], StringComparison.Ordinal))
        throw new Exception("The launch ticket was written as plain text.");
    Console.WriteLine(path);
    return 0;
}

if (args.Length == 4 && args[0] == "--account-check")
{
    using var checkApi = new LauncherApi(args[1]);
    await checkApi.LoginAsync(args[2], args[3], CancellationToken.None);
    var catalog = await checkApi.GetProgramsAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new { checkApi.Account, checkApi.AccountExpired, Programs = catalog.Count }));
    return 0;
}

if (args.Length is 1 or 2 && args[0] == "--preview")
{
    var livePreview = args.Length == 2 && args[1] == "live";
    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    try { Application.EnableVisualStyles(); } catch (UnauthorizedAccessException) { }
    Application.SetCompatibleTextRenderingDefault(false);
    using var form = new LauncherForm(new LauncherApi("http://127.0.0.1:5115"), new Installer())
    {
        ShowInTaskbar = livePreview, Opacity = livePreview ? 1 : 0
    };
    form.Show();
    Application.DoEvents();
    var layoutScale = args.Length == 2 && !livePreview ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1f;
    if (layoutScale != 1f)
    {
        form.MinimumSize = form.MaximumSize = Size.Empty;
        UiChecks.ChangeDpi(form, (int)Math.Round(96 * layoutScale));
        Application.DoEvents();
    }
    var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".local"));
    if (layoutScale != 1f) output = Path.Combine(output, "ui-scale-" + layoutScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Directory.CreateDirectory(output);
    using (var loginBitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(loginBitmap, new Rectangle(Point.Empty, loginBitmap.Size));
        loginBitmap.Save(Path.Combine(output, "ui-login.png"));
    }
    var previewPrograms = new[] { "chispa", "tool-slot", "beta-slot" }
        .Select(id => new ProgramInfo(id, id == "chispa" ? "Chispa" : id == "tool-slot" ? "扩展程序位" : "Beta 测试位",
            "Preview.exe", "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets",
            id == "chispa" ? "branding" : "icons", id + ".png"))), IsBeta: id == "beta-slot", HasRelease: id == "chispa"))
        .ToArray();
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var previewApi = (LauncherApi)typeof(LauncherForm).GetField("api", flags)!.GetValue(form)!;
    typeof(LauncherApi).GetProperty("Account")!.SetValue(previewApi,
        new AccountInfo("Chispa", DateTimeOffset.UtcNow.AddDays(12).AddHours(3), DateTimeOffset.UtcNow));
    typeof(LauncherApi).GetField("accountReceivedAt", flags)!.SetValue(previewApi, System.Diagnostics.Stopwatch.GetTimestamp());
    typeof(LauncherForm).GetMethod("UpdateAccountTime", flags)!.Invoke(form, null);
    typeof(LauncherForm).GetMethod("LoadProgramCards", flags)!.Invoke(form, [previewPrograms]);
    typeof(LauncherForm).GetField("selected", flags)!.SetValue(form, previewPrograms[0]);
    typeof(LauncherForm).GetField("latest", flags)!.SetValue(form, new Release("chispa", "1.2.0", "Preview.exe", "", 100));
    ((Label)typeof(LauncherForm).GetField("welcome", flags)!.GetValue(form)!).Text = "欢迎，Chispa";
    ((Label)typeof(LauncherForm).GetField("catalogCount", flags)!.GetValue(form)!).Text = "3 个已授权选项";
    ((Label)typeof(LauncherForm).GetField("libraryStatus", flags)!.GetValue(form)!).Text = "程序库已同步。选择程序，开始新的体验。";
    ((Panel)typeof(LauncherForm).GetField("loginPage", flags)!.GetValue(form)!).Visible = false;
    ((Panel)typeof(LauncherForm).GetField("libraryPage", flags)!.GetValue(form)!).Visible = true;
    typeof(LauncherForm).GetMethod("MarkSelection", flags)!.Invoke(form, null);
    Application.DoEvents();
    UiChecks.CheckLibrary(form);
    UiChecks.CheckText(form);
    if (layoutScale == 1f) UiChecks.CheckQuietRefresh(form, previewPrograms);
    if (livePreview)
    {
        form.Text = "Chispa Launcher — UI Preview 1.0.3";
        ((System.Windows.Forms.Timer)typeof(LauncherForm).GetField("accountTimer", flags)!.GetValue(form)!).Start();
        Application.Run(form);
        return 0;
    }
    using (var libraryBitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(libraryBitmap, new Rectangle(Point.Empty, libraryBitmap.Size));
        libraryBitmap.Save(Path.Combine(output, "ui-library.png"));
    }
    typeof(LauncherApi).GetProperty("Account")!.SetValue(previewApi,
        new AccountInfo("Chispa", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow));
    typeof(LauncherApi).GetField("accountReceivedAt", flags)!.SetValue(previewApi, System.Diagnostics.Stopwatch.GetTimestamp());
    typeof(LauncherForm).GetMethod("UpdateAccountTime", flags)!.Invoke(form, null);
    typeof(LauncherForm).GetMethod("UpdateControls", flags)!.Invoke(form, null);
    var timeLabel = (Label)typeof(LauncherForm).GetField("accountTime", flags)!.GetValue(form)!;
    if (!timeLabel.Text.Contains("已到期")) throw new Exception("Expired account label missing");
    if (((Button)typeof(LauncherForm).GetField("install", flags)!.GetValue(form)!).Enabled)
        throw new Exception("Expired account can install");
    Application.DoEvents();
    using (var expiredBitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(expiredBitmap, new Rectangle(Point.Empty, expiredBitmap.Size));
        expiredBitmap.Save(Path.Combine(output, "ui-library-expired.png"));
    }
    UiChecks.CheckRegistration(form, output);
    form.Close();
    Console.WriteLine("UI previews saved.");
    return 0;
}

if (args.Length != 1) throw new ArgumentException("Expected local server URL.");
using var api = new LauncherApi(args[0]);
var installer = new Installer(trustKeys);
if (await api.CheckConnectivityAsync(CancellationToken.None) < TimeSpan.Zero)
    throw new Exception("Connectivity probe returned invalid time.");
var credentialPath = Environment.GetEnvironmentVariable("CHISPA_CREDENTIAL_FILE")
    ?? throw new Exception("Test credential path is required.");
CredentialStore.Save(args[0], "testuser", "test account password");
var saved = CredentialStore.Load(args[0]);
if (saved is null || saved.Value.Username != "testuser" || saved.Value.Password != "test account password")
    throw new Exception("Saved credentials could not be restored.");
if (File.ReadAllText(credentialPath).Contains("test account password", StringComparison.Ordinal))
    throw new Exception("Password was stored as plain text.");
if (CredentialStore.Load(args[0] + "/other") is not null)
    throw new Exception("Credentials were offered to a different server.");
CredentialStore.Delete();
await api.LoginAsync("testuser", "correct horse battery staple", CancellationToken.None);
var programs = await api.GetProgramsAsync(CancellationToken.None);
if (programs.Count != 2) throw new Exception("Expected two programs after login.");
foreach (var program in programs.Where(p => p.HasRelease != false))
{
    var release = await api.GetLatestAsync(program.Id, CancellationToken.None);
    await installer.InstallAsync(api, program, release, null, CancellationToken.None);
    if (installer.InstalledVersion(program) != release.Version)
        throw new Exception($"Client did not activate {program.Id}.");
    await installer.InstallAsync(api, program, release, null, CancellationToken.None);
    var directory = await installer.VerifyInstalledAsync(api, program, CancellationToken.None);
    var executable = Path.Combine(directory, program.Executable);
    var original = File.ReadAllBytes(executable);
    File.AppendAllText(executable, "tampered");
    await MustReject(() => installer.VerifyInstalledAsync(api, program, CancellationToken.None), "Modified executable was accepted");
    await installer.InstallAsync(api, program, release, null, CancellationToken.None);
    if (!File.ReadAllBytes(executable).SequenceEqual(original)) throw new Exception("Repair did not restore the original executable");
    var extra = Path.Combine(directory, "unpublished.dll");
    File.WriteAllText(extra, "unexpected binary");
    await MustReject(() => installer.VerifyInstalledAsync(api, program, CancellationToken.None), "Unexpected DLL was accepted");
    File.Delete(extra);
    var dll = Directory.EnumerateFiles(directory, "*.dll").FirstOrDefault();
    if (dll is not null)
    {
        var contents = File.ReadAllBytes(dll);
        File.Delete(dll);
        await MustReject(() => installer.VerifyInstalledAsync(api, program, CancellationToken.None), "Missing DLL was accepted");
        File.WriteAllBytes(dll, contents);
    }
    await installer.VerifyInstalledAsync(api, program, CancellationToken.None);
    // The credential the loader hands to the program. This is the loader half of task 02: the
    // service must issue a short-lived, single-use ticket for exactly this program and version,
    // and it must refuse one for a version that was never published.
    var ticket = await api.RequestLaunchTicketAsync(program.Id, release.Version, CancellationToken.None);
    if (string.IsNullOrWhiteSpace(ticket.Ticket) || ticket.ExpiresAt <= DateTimeOffset.UtcNow)
        throw new Exception("The loader received an unusable launch ticket.");
    try
    {
        await api.RequestLaunchTicketAsync(program.Id, "9.9.9", CancellationToken.None);
        throw new Exception("A launch ticket was issued for an unpublished version");
    }
    catch (InvalidOperationException) { }
}
Console.WriteLine("PASS: client installed two programs in separate version directories");
Console.WriteLine("PASS: launch verification rejects modified EXE, missing DLL and extra DLL");
Console.WriteLine("PASS: loader obtains a single-use launch ticket and refuses an unpublished version");
await api.LogoutAsync(CancellationToken.None);
try
{
    await installer.VerifyInstalledAsync(api, programs[0], CancellationToken.None);
    throw new Exception("Logged-out client could verify a launch");
}
catch (InvalidOperationException) { }
Console.WriteLine("PASS: logged-out client cannot launch installed programs");
return 0;

static async Task MustReject(Func<Task<string>> action, string message)
{
    try { await action(); }
    catch (InvalidDataException) { return; }
    throw new Exception(message);
}
