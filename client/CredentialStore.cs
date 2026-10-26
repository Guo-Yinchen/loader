using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chispa.Launcher;

internal static class CredentialStore
{
    private static readonly byte[] Entropy = "Chispa.Launcher.Credentials.v1"u8.ToArray();
    private static readonly string PathName = Environment.GetEnvironmentVariable("CHISPA_CREDENTIAL_FILE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Chispa", "launcher-credentials.json");
    private sealed record Saved(string ServerUrl, string Username, string PasswordCipher);

    public static (string Username, string Password)? Load(string serverUrl)
    {
        try
        {
            if (!File.Exists(PathName)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(PathName));
            if (saved is null || saved.ServerUrl != serverUrl) return null;
            var encrypted = Convert.FromBase64String(saved.PasswordCipher);
            var password = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return (saved.Username, Encoding.UTF8.GetString(password));
        }
        catch { return null; }
    }

    public static void Save(string serverUrl, string username, string password)
    {
        var directory = Path.GetDirectoryName(PathName)!;
        Directory.CreateDirectory(directory);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        var saved = new Saved(serverUrl, username, Convert.ToBase64String(encrypted));
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved));
            File.Move(temporary, PathName, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Delete()
    {
        if (File.Exists(PathName)) File.Delete(PathName);
    }
}
