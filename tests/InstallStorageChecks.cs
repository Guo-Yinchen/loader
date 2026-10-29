using System.Diagnostics;
using System.Text.Json;
using Chispa.Launcher;

internal static class InstallStorageChecks
{
    internal static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "chispa-storage-" + Guid.NewGuid().ToString("N")));
        var previousInstall = Environment.GetEnvironmentVariable("CHISPA_INSTALL_ROOT");
        var previousConfig = Environment.GetEnvironmentVariable("CHISPA_CONFIG_DIRECTORY");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("CHISPA_INSTALL_ROOT", root);
            var data = Path.Combine(root, "wardogs");
            Environment.SetEnvironmentVariable("CHISPA_CONFIG_DIRECTORY", data);
            var program = new ProgramInfo("chispa", "Wardogs", "Wardogs.exe");
            var installer = new Installer();
            var versions = Path.Combine(root, "versions");
            var current = Path.Combine(versions, "1.0.0");
            Put(Path.Combine(current, "Wardogs.exe"), "old exe");
            Put(Path.Combine(current, "Wardogs.ini"), "old defaults");
            Put(Path.Combine(current, "Wardogs-Combat.ini"), "selected settings");
            Put(Path.Combine(current, "Wardogs.profile"), "Combat\n");
            Put(Path.Combine(current, "logs", "layout.ini"), "window layout");
            Put(Path.Combine(versions, "0.9.0", "Wardogs.exe"), "older exe");
            Put(Path.Combine(root, "backups", "1.0.0-" + Guid.NewGuid().ToString("N"), "Wardogs-Extra.ini"), "extra settings");
            var pointer = Path.Combine(root, "current.json");
            Put(pointer, "{\"Version\":\"1.0.0\"}");
            Put(Path.Combine(data, "Wardogs.ini"), "existing user settings");
            var helper = Path.Combine(root, "programs", "helper", "versions", "2.0.0", "Helper.exe");
            Put(helper, "other program");
            var unknown = Path.Combine(versions, "notes", "keep.txt");Put(unknown, "personal file");

            installer.ActivatePackage(program, "1.1.0", Stage(versions, "new exe"));
            Check(installer.InstalledVersion(program) == "1.1.0", "new pointer activates after extraction");
            Check(!Directory.Exists(current) && !Directory.Exists(Path.Combine(versions, "0.9.0")), "old versions are removed");
            Check(!Directory.Exists(Path.Combine(root, "backups")), "legacy backup archives are removed");
            Check(File.ReadAllText(Path.Combine(data, "Wardogs.ini")) == "existing user settings", "user defaults are never overwritten");
            Check(File.ReadAllText(Path.Combine(data, "Wardogs-Combat.ini")) == "selected settings"
                && File.ReadAllText(Path.Combine(data, "Wardogs.profile")).Trim() == "Combat", "named and active profiles migrate");
            Check(File.ReadAllText(Path.Combine(data, "layout.ini")) == "window layout"
                && File.Exists(Path.Combine(data, "Wardogs-Extra.ini")), "layout and unique backup profiles migrate before cleanup");
            Check(File.Exists(helper) && File.Exists(unknown), "other programs and unknown directories are untouched");

            File.Delete(Path.Combine(data, "Wardogs-Combat.ini"));
            Put(Path.Combine(versions, "1.1.0", "Wardogs-Combat.ini"), "stale profile");
            installer.ActivatePackage(program, "1.1.0", Stage(versions, "repaired exe"));
            Check(!File.Exists(Path.Combine(data, "Wardogs-Combat.ini")), "deleted profiles do not reappear on repair");
            Check(!Directory.EnumerateDirectories(versions, ".replace-*").Any()
                && !Directory.Exists(Path.Combine(root, "backups")), "repair retains no program backup");

            // A locked current pointer simulates a commit failure after the directory swap.
            using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
                Refuse(() => installer.ActivatePackage(program, "1.1.0", Stage(versions, "failed repair")), "locked pointer must stop commit");
            Check(File.ReadAllText(Path.Combine(versions, "1.1.0", "Wardogs.exe")) == "repaired exe"
                && installer.InstalledVersion(program) == "1.1.0", "failed repair restores the previous directory and pointer");
            using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
                Refuse(() => installer.ActivatePackage(program, "1.2.0", Stage(versions, "failed update")), "update commit must fail");
            Check(!Directory.Exists(Path.Combine(versions, "1.2.0")) && installer.InstalledVersion(program) == "1.1.0",
                "failed update keeps the active installation");

            var interrupted = Path.Combine(versions, ".replace-1.1.0-" + Guid.NewGuid().ToString("N"));
            Directory.Move(Path.Combine(versions, "1.1.0"), interrupted);
            Installer.RecoverInterruptedReplacement(versions);
            Check(installer.InstalledVersion(program) == "1.1.0", "an interrupted repair can recover before retry");

            var blocked = Path.Combine(root, "blocked-settings");Put(blocked, "not a directory");
            Environment.SetEnvironmentVariable("CHISPA_CONFIG_DIRECTORY", blocked);
            var waiting = Stage(versions, "not activated");
            Refuse(() => installer.ActivatePackage(program, "1.2.0", waiting), "failed migration must stop activation");
            Check(Directory.Exists(waiting) && installer.InstalledVersion(program) == "1.1.0", "migration failure does not delete the active version");
            Environment.SetEnvironmentVariable("CHISPA_CONFIG_DIRECTORY", data);
            Refuse(() => installer.ActivatePackage(program, "1.2.0", Path.Combine(root, "outside-stage")), "staging cannot escape versions");

            // Run a harmless stdin-waiting Windows command under the installed EXE name.
            var executable = Path.Combine(versions, "1.1.0", "Wardogs.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable, overwrite: true);
            using (var process = Process.Start(new ProcessStartInfo(executable, "/d /c set /p waiting=")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!)
            {
                try
                {
                    Check(!process.WaitForExit(200), "running-process fixture waits for stdin");
                    Refuse(() => installer.ActivatePackage(program, "1.2.0", waiting), "running programs must block update");
                    Check(File.Exists(executable), "running installation remains intact");
                }
                finally { process.StandardInput.WriteLine("done");process.StandardInput.Close();process.WaitForExit(5000); }
            }
            Console.WriteLine("PASS: install storage migration, upgrade/repair cleanup, rollback, crash recovery, isolation and running-process protection");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHISPA_INSTALL_ROOT", previousInstall);
            Environment.SetEnvironmentVariable("CHISPA_CONFIG_DIRECTORY", previousConfig);
            var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(root) != parent || !Path.GetFileName(root).StartsWith("chispa-storage-"))
                throw new IOException("Unexpected test cleanup target");
            Installer.AssertPlainTree(root);
            Directory.Delete(root, recursive: true);
        }
    }
    private static string Stage(string versions, string content)
    {
        var path = Path.Combine(versions, ".stage-" + Guid.NewGuid().ToString("N"));
        Put(Path.Combine(path, "Wardogs.exe"), content);return path;
    }
    private static void Put(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path, text); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Refuse(Action action, string message)
    {
        try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        throw new Exception(message);
    }
}
