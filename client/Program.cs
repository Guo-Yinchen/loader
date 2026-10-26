using System.Reflection;

namespace Chispa.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            try { Application.EnableVisualStyles(); }
            catch (UnauthorizedAccessException ex) { LogError(ex); }
            Application.SetCompatibleTextRenderingDefault(false);
            var serverUrl = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .First(a => a.Key == "ChispaServerUrl").Value
                ?? "http://127.0.0.1:5115";
            Application.Run(new LauncherForm(new LauncherApi(serverUrl), new Installer()));
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(ex.Message, "Chispa Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
