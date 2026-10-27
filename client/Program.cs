using System.Reflection;

namespace Chispa.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var serverUrl = ServerUrl();
        if (HandleVerificationArguments(args, serverUrl)) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            try { Application.EnableVisualStyles(); }
            catch (UnauthorizedAccessException ex) { LogError(ex); }
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm(new LauncherApi(serverUrl), new Installer()));
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(ex.Message, "Chispa Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ServerUrl() =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ChispaServerUrl")?.Value
            ?? "http://127.0.0.1:5115";

    // Packaging checks run the finished EXE instead of searching its bytes: a compressed
    // single-file build hides the assembly, so only the shipped binary can prove which
    // server address it will actually contact. Switches exit before any UI is created, and
    // an unknown "--" argument fails instead of silently opening the window.
    private static bool HandleVerificationArguments(string[] args, string serverUrl)
    {
        if (args.Length == 0 || !args[0].StartsWith("--", StringComparison.Ordinal)) return false;
        try
        {
            switch (args[0])
            {
                case "--print-server-url" when args.Length == 1:
                    Console.Out.WriteLine(serverUrl);
                    return true;
                case "--print-server-url" when args.Length == 2:
                    File.WriteAllText(args[1], serverUrl + Environment.NewLine);
                    return true;
                case "--verify-server-url" when args.Length == 2:
                    var matches = string.Equals(serverUrl.TrimEnd('/'), args[1].Trim().TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase);
                    Environment.ExitCode = matches ? 0 : 1;
                    return true;
                default:
                    Console.Error.WriteLine("未知的参数：" + string.Join(" ", args));
                    Console.Error.WriteLine("支持：--print-server-url [文件] / --verify-server-url <地址>");
                    Environment.ExitCode = 2;
                    return true;
            }
        }
        catch (Exception ex)
        {
            LogError(ex);
            Environment.ExitCode = 1;
            return true;
        }
    }

    private static void LogError(Exception? error)
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("CHISPA_LAUNCHER_LOG_DIRECTORY")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chispa");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "launcher-error.log"),
                DateTimeOffset.Now + Environment.NewLine + error + Environment.NewLine);
        }
        catch { }
    }
}
