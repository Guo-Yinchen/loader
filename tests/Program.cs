using Chispa.Launcher;
using System.Reflection;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

if (args.Length == 1 && args[0] == "--preview")
{
    try { Application.EnableVisualStyles(); } catch (UnauthorizedAccessException) { }
    Application.SetCompatibleTextRenderingDefault(false);
    using var form = new LauncherForm(new LauncherApi("http://127.0.0.1:5115"), new Installer())
    {
        ShowInTaskbar = false, Opacity = 0
    };
    form.Show();
    Application.DoEvents();
    var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".local"));
    Directory.CreateDirectory(output);
    using (var loginBitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(loginBitmap, new Rectangle(Point.Empty, loginBitmap.Size));
        loginBitmap.Save(Path.Combine(output, "ui-login.png"));
    }
    var previewPrograms = new[] { "chispa", "tool-slot", "beta-slot" }
        .Select(id => new ProgramInfo(id, id == "chispa" ? "Chispa" : id == "tool-slot" ? "扩展程序位" : "Beta 测试位",
            "Preview.exe", "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(Path.Combine(output, "..", "assets",
            id == "chispa" ? "branding" : "icons", id + ".png"))), IsBeta: id == "beta-slot", HasRelease: id == "chispa"))
        .ToArray();
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
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
    using (var libraryBitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(libraryBitmap, new Rectangle(Point.Empty, libraryBitmap.Size));
        libraryBitmap.Save(Path.Combine(output, "ui-library.png"));
    }
    form.Close();
    Console.WriteLine("UI previews saved.");
    return;
}

if (args.Length != 1) throw new ArgumentException("Expected local server URL.");
using var api = new LauncherApi(args[0]);
var installer = new Installer();
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
}
Console.WriteLine("PASS: client installed two programs in separate version directories");
Console.WriteLine("PASS: launch verification rejects modified EXE, missing DLL and extra DLL");
await api.LogoutAsync(CancellationToken.None);
try
{
    await installer.VerifyInstalledAsync(api, programs[0], CancellationToken.None);
    throw new Exception("Logged-out client could verify a launch");
}
catch (InvalidOperationException) { }
Console.WriteLine("PASS: logged-out client cannot launch installed programs");

static async Task MustReject(Func<Task<string>> action, string message)
{
    try { await action(); }
    catch (InvalidDataException) { return; }
    throw new Exception(message);
}
