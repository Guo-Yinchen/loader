using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chispa.Launcher;

// How a short-lived launch ticket reaches the program without ever appearing anywhere a
// secret should not be.
//
// Not on the command line: a command line is world-readable through WMI and lands in every
// process listing, shell history and crash dump around it. Not in the environment block for
// the same reason. So the loader writes one DPAPI-protected file per launch, under the
// current user's profile, and the program reads it, deletes it and redeems it. The file name
// carries the program id (no secret) and the ticket inside is single-use for two minutes.
//
// ProtectedData is the Windows DPAPI at user scope, invoked directly: the .NET wrapper for it
// ships as a separate package on this target framework, and a launch ticket is not worth a
// new dependency.
internal static class LaunchTicketFile
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Chispa.LaunchTicket.v1");

    // The directory that holds the hand-off, and the ONE definition of it on this side: the C++
    // program computes the same path from the same variable. CHISPA_AUTH_DIR replaces the whole
    // directory rather than being a parent of it - the two sides agree byte for byte or the
    // ticket is never found, which is exactly the failure this comment exists to prevent.
    public static string Directory => Environment.GetEnvironmentVariable("CHISPA_AUTH_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chispa", "auth");

    public static string PathFor(string programId)
    {
        LauncherApi.ValidateProgramId(programId);
        return Path.Combine(Directory, $"ticket-{programId}.bin");
    }

    // Every launch starts by discarding hand-offs that were never consumed (the user declined
    // the UAC prompt, the process died before it read the file, the machine lost power). They
    // are already worthless - two minutes and one redemption - but leaving them around turns
    // a mystery "authorization failed" into an obvious one.
    public static void CleanupStale(TimeSpan? age = null)
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var cutoff = DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(10));
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "ticket-*.bin"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string Write(string programId, string serverUrl, string version,
        LaunchTicket ticket, CancellationToken cancellationToken, bool uniqueName = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CleanupStale();
        System.IO.Directory.CreateDirectory(Directory);
        var envelope = JsonSerializer.Serialize(new TicketEnvelope(1, serverUrl.TrimEnd('/'), programId,
            version, ticket.Ticket, ticket.ExpiresAt));
        var payload = Protect(Encoding.UTF8.GetBytes(envelope));
        var path = uniqueName
            ? Path.Combine(Directory, $"ticket-{programId}-{Guid.NewGuid():N}.bin") : PathFor(programId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    // Used when the program could not be started at all: it never had a chance to consume
    // the ticket, so the next launch must not accidentally pick this one up.
    public static void Delete(string programId)
    {
        try { var path = PathFor(programId); if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Field names are pinned, not left to the serializer's defaults: the program that reads this
    // file is C++ and its reader is written against these exact keys.
    internal sealed record TicketEnvelope(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("server")] string Server,
        [property: JsonPropertyName("programId")] string ProgramId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("ticket")] string Ticket,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

    private static byte[] Protect(byte[] plain)
    {
        var input = Blob(plain, out var inputHandle);
        var entropy = Blob(Entropy, out var entropyHandle);
        try
        {
            if (!CryptProtectData(ref input, "Chispa launch ticket", ref entropy, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output))
                throw new InvalidOperationException("无法保护启动凭证（DPAPI 失败）。");
            try { return Read(output); }
            finally { LocalFree(output.pbData); }
        }
        finally
        {
            Marshal.FreeHGlobal(inputHandle);
            Marshal.FreeHGlobal(entropyHandle);
        }
    }

    private static byte[] Read(DATA_BLOB blob)
    {
        var buffer = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, buffer, 0, blob.cbData);
        return buffer;
    }

    private static DATA_BLOB Blob(byte[] data, out IntPtr handle)
    {
        handle = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, handle, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = handle };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string description,
        ref DATA_BLOB entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);
}
