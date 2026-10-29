namespace Chispa.Launcher;

// Shared on-disk contract with Wardogs Config: user data is outside all version folders.
internal static class WardogsSettings
{
    internal static string DirectoryPath => Environment.GetEnvironmentVariable("CHISPA_CONFIG_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chispa", "wardogs");

    internal static void Migrate(IEnumerable<string> legacyDirectories)
    {
        var target = Path.GetFullPath(DirectoryPath);
        Installer.AssertPlainTree(target);
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, ".storage-v1");
        if (File.Exists(marker)) return;
        foreach (var legacy in legacyDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(legacy)) continue;
            Installer.AssertPlainTree(legacy);
            foreach (var source in Directory.EnumerateFiles(legacy))
            {
                var name = Path.GetFileName(source);
                if (name.Equals("Wardogs.ini", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Wardogs.profile", StringComparison.OrdinalIgnoreCase)
                    || (name.StartsWith("Wardogs-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("Wardogs-layout.ini", StringComparison.OrdinalIgnoreCase)))
                    CopyMissing(source, Path.Combine(target, name));
            }
            CopyMissing(Path.Combine(legacy, "logs", "layout.ini"), Path.Combine(target, "layout.ini"));
            CopyMissing(Path.Combine(legacy, "Wardogs-layout.ini"), Path.Combine(target, "layout.ini"));
        }
        File.WriteAllText(marker, "chispa-wardogs-settings-v1\n");
    }

    private static void CopyMissing(string source, string destination)
    {
        if (!File.Exists(source) || File.Exists(destination)) return;
        // Publish each imported file atomically, never leaving a half-written config in force.
        var temporary = destination + ".migrate-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
