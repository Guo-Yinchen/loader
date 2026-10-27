using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Chispa.Release;

namespace Chispa.Launcher;

internal sealed class Installer
{
    private readonly string root = Environment.GetEnvironmentVariable("CHISPA_INSTALL_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chispa");
    private readonly IReadOnlyList<TrustedReleaseKey> trustKeys;

    // The production entry point uses the keys compiled into this build (client/ReleaseTrust.cs).
    // The overload exists so the test project can verify against a throwaway key it generated
    // itself; nothing reads a key list from the environment, a file or the server.
    public Installer() : this(ReleaseTrust.Keys) { }

    internal Installer(IReadOnlyList<TrustedReleaseKey> trustKeys) => this.trustKeys = trustKeys;

    /// <summary>Verifies the release signature and that it signs exactly the manifest the caller is
    /// about to act on. Returns the signed statement so callers can bind further claims to it.</summary>
    internal SignedReleaseFields VerifySignature(Release release)
    {
        if (!ReleaseSignature.TryOpen(release.Signature, trustKeys, out var signed, out _, out var error))
            throw new InvalidDataException(error);
        if (!ReleaseSignature.Matches(signed!, release.ProgramId, release.Version, release.Executable,
                release.Size, release.Sha256, out error))
            throw new InvalidDataException(error);
        return signed!;
    }

    public string? InstalledVersion(ProgramInfo program)
    {
        LauncherApi.ValidateProgram(program);
        var programRoot = ProgramRoot(program.Id);
        var pointer = Path.Combine(programRoot, "current.json");
        if (!File.Exists(pointer)) return null;
        try
        {
            var version = JsonSerializer.Deserialize<Installed>(File.ReadAllText(pointer))?.Version;
            if (version is null || !System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+$") || !Version.TryParse(version, out _)) return null;
            return File.Exists(Path.Combine(programRoot, "versions", version, program.Executable)) ? version : null;
        }
        catch { return null; }
    }

    public async Task InstallAsync(LauncherApi api, ProgramInfo program, Release release, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        Validate(program, release);
        if (!Version.TryParse(release.Version, out _) || release.Size <= 0 || release.Size > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Invalid release manifest.");
        // Before a single byte is downloaded: is this manifest what the release environment signed?
        // Doing it first also means the SHA-256 the download will be compared against is the signed
        // one, not a number the server made up.
        VerifySignature(release);
        var programRoot = ProgramRoot(program.Id);
        var versions = Path.Combine(programRoot, "versions");
        Directory.CreateDirectory(versions);
        // One installer per program directory. Starting the loader twice (easy: double-click) must
        // not have two downloads and two directory moves racing over the same version folder.
        FileStream installLock;
        try
        {
            installLock = new FileStream(Path.Combine(programRoot, ".install.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("这个程序正在由另一个安装任务处理，请等它完成后再试。");
        }
        using (installLock)
        {
            DeleteStaleStages(versions);
            var destination = Path.Combine(versions, release.Version);
            // The signed package identity stays stable across retries and app restarts.
            var tempZip = Path.Combine(programRoot, "download-" + release.Version + "-" +
                release.Sha256.ToLowerInvariant() + ".zip");
            var stage = Path.Combine(versions, ".stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                await api.DownloadAsync(release, tempZip, progress, cancellationToken);
                await using (var stream = File.OpenRead(tempZip))
                {
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                    if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("下载的发布包与清单哈希不符，已停止安装（文件未改动）。");
                }

                Directory.CreateDirectory(stage);
                using (var archive = ZipFile.OpenRead(tempZip))
                {
                    if (archive.Entries.Count > 20_000) throw new InvalidDataException("发布包文件数量过多。");
                    long uncompressed = 0;
                    foreach (var entry in archive.Entries)
                    {
                        uncompressed += entry.Length;
                        if (uncompressed > 4L * 1024 * 1024 * 1024)
                            throw new InvalidDataException("Uncompressed package is too large.");
                        var full = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                        if (!full.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Package contains an unsafe path.");
                    }
                    LauncherApi.EnsureDiskSpace(stage, uncompressed);
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                        if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await using var input = entry.Open();
                        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        await input.CopyToAsync(output, cancellationToken);
                    }
                }

                if (!File.Exists(Path.Combine(stage, program.Executable)))
                    throw new InvalidDataException($"Package is missing {program.Executable}.");

                File.WriteAllText(Path.Combine(stage, ".package.sha256"), release.Sha256);
                string? backup = null;
                if (Directory.Exists(destination))
                {
                    // Re-download for repairs. Preserve the old directory so runtime
                    // settings can be recovered; never silently trust a local marker.
                    var backups = Path.Combine(programRoot, "backups");
                    Directory.CreateDirectory(backups);
                    backup = Path.Combine(backups, release.Version + "-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(destination, backup);
                }
                try { Directory.Move(stage, destination); }
                catch
                {
                    if (backup is not null) Directory.Move(backup, destination);
                    throw;
                }
                PointTo(programRoot, release.Version);
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
        }
    }

    /// <summary>Removes staging directories left behind by a crashed or killed install. Only
    /// `.stage-*` entries are touched, and only ones older than an hour, so a concurrent install in
    /// progress is never deleted by this cleanup.</summary>
    private static void DeleteStaleStages(string versions)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(versions, ".stage-*"))
            {
                var info = new DirectoryInfo(directory);
                if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromHours(1)) continue;
                info.Delete(recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task LaunchAsync(LauncherApi api, ProgramInfo program, CancellationToken cancellationToken)
    {
        var directory = await VerifyInstalledAsync(api, program, cancellationToken);
        var installed = InstalledVersion(program) ?? throw new InvalidOperationException("请先下载并安装程序。");
        cancellationToken.ThrowIfCancellationRequested();
        // The account proof stops at the loader unless the program can prove it too. The
        // credential rides in a DPAPI-protected file under the user's profile - never on the
        // command line, never in the environment block - and the program deletes it on read.
        var ticket = await api.RequestLaunchTicketAsync(program.Id, installed, cancellationToken);
        var ticketPath = LaunchTicketFile.Write(program.Id, api.ServerUrl, installed, ticket, cancellationToken,
            uniqueName: true);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(directory, program.Executable),
                WorkingDirectory = directory,
                UseShellExecute = true,
                Verb = "runas"
            };
            // Only the path is public. The single-use credential remains protected by DPAPI.
            // Pass it explicitly so UAC/UIAccess restarts do not have to rediscover a profile path.
            start.ArgumentList.Add("--chispa-ticket");
            start.ArgumentList.Add(ticketPath);
            if (Process.Start(start) is null) throw new InvalidOperationException("程序未能启动。");
        }
        catch
        {
            // Never leave a credential behind for a start that did not happen.
            try { File.Delete(ticketPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // Never authorize a launch from a local marker: the server rechecks the live
    // account, expiry, Beta flag and scope, and supplies hashes from its ZIP. The signature check
    // below is what makes those hashes trustworthy: the per-file map is covered by
    // `binariesSha256` in the signed statement, so a compromised server cannot answer "yes, that
    // modified DLL is the published one".
    internal async Task<string> VerifyInstalledAsync(LauncherApi api, ProgramInfo program, CancellationToken cancellationToken)
    {
        var installed = InstalledVersion(program) ?? throw new InvalidOperationException("请先下载并安装程序。");
        var release = await api.AuthorizeLaunchAsync(program.Id, installed, cancellationToken);
        Validate(program, release);
        if (release.Version != installed || release.Binaries is not { Count: > 0 } binaries ||
            !binaries.ContainsKey(program.Executable))
            throw new InvalidDataException("服务器的启动验证信息不完整，请更新服务端。");
        var signed = VerifySignature(release);
        ReleaseSignature.CompareBinaries(signed, binaries);
        var directory = Path.GetFullPath(Path.Combine(ProgramRoot(program.Id), "versions", installed));
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("程序目录包含链接，请重新安装到普通目录。");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in binaries)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.Contains(':') ||
                relative.Split('/').Any(part => part is "" or "." or "..") || !ProtectedBinary(relative) ||
                hash is null || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") ||
                !expected.TryAdd(relative, hash))
                throw new InvalidDataException("服务器的文件验证清单无效。");
        }
        var pending = new Stack<string>();
        pending.Push(directory);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var folder))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("程序目录包含链接，请重新安装。");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                if (!ProtectedBinary(path)) continue;
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (!expected.TryGetValue(relative, out var hash))
                    throw new InvalidDataException("程序目录中存在未发布的可执行文件，请重新安装。");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!digest.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("程序文件已被修改，已阻止启动。请重新安装。");
                actual.Add(relative);
            }
        }
        if (actual.Count != expected.Count)
            throw new InvalidDataException("程序文件不完整，请重新安装。");
        return directory;
    }

    private static bool ProtectedBinary(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".sys" or ".ocx" or ".com";

    private void PointTo(string programRoot, string version)
    {
        var pointer = Path.Combine(programRoot, "current.json");
        var temporary = pointer + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Installed(version)));
        File.Move(temporary, pointer, overwrite: true);
    }

    private string ProgramRoot(string programId)
    {
        LauncherApi.ValidateProgramId(programId);
        // Retain access to installations created by the original Chispa-only launcher.
        return programId == "chispa" ? root : Path.Combine(root, "programs", programId);
    }

    private static void Validate(ProgramInfo program, Release release)
    {
        LauncherApi.ValidateProgram(program);
        if (release.ProgramId != program.Id || release.Executable != program.Executable)
            throw new InvalidDataException("Release does not match the selected program.");
    }

    private sealed record Installed(string Version);
}
