using System.IO.Compression;
using System.Security.Cryptography;
using Chispa.Release;

namespace Chispa.StoreInternal;

// The signed-publish path, split out of Program.cs because it is the one part of the store that
// has to be read as a whole: what gets verified, in which order, and what happens to a release
// that was published before signatures existed.
//
// THE ORDER MATTERS AND IT IS THE POINT OF THE FILE.
//   1. the signature must verify against a trusted public key  (otherwise: nothing else matters)
//   2. the fields it signed must equal the package bytes on disk (size, SHA-256, per-file digest)
//   3. the fields must equal the program and version being published
//   4. only then is the package stored, byte for byte as uploaded
// Step 4 never rewrites the ZIP - a normalisation pass would change bytes the signature covers,
// which is why the package is built by the signing tool and this server only accepts or refuses.
internal static class SignedPublish
{
    public sealed record Verified(SignedReleaseFields Fields, string KeyId, long Size, string Sha256,
        Dictionary<string, string> Binaries);

    public static Verified Verify(string programId, string version, string executable,
        string packagePath, string signatureJson, IReadOnlyList<TrustedReleaseKey> trustKeys)
    {
        // Decoding the envelope, checking the key id against the trusted list and verifying the
        // signature over the exact statement bytes all happen inside TryOpen.
        if (!ReleaseSignature.TryOpen(ReleaseSignature.ToTransport(signatureJson), trustKeys,
                out var signed, out var keyId, out var error))
            throw new InvalidDataException(error);

        // Everything below is measured from the package on disk, never from the arguments of the
        // signing command: the question is "does this file match what was signed", not "did the
        // operator type the same numbers twice".
        var (size, sha256, binaries) = DescribePackage(packagePath);
        if (!ReleaseSignature.Matches(signed!, programId, version, executable, size, sha256, out error))
            throw new InvalidDataException(error);
        if (!string.Equals(ReleaseSignature.BinariesDigest(binaries), signed!.BinariesSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("签名里的文件清单与包内容不一致，拒绝发布。");

        Layout(packagePath, executable);
        return new Verified(signed, keyId, size, sha256, binaries);
    }

    public static (long Size, string Sha256, Dictionary<string, string> Binaries) DescribePackage(string packagePath)
    {
        using var stream = File.OpenRead(packagePath);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var size = stream.Length;
        stream.Position = 0;
        var binaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = OpenArchive(stream))
        {
            foreach (var entry in archive.Entries)
            {
                if (!ReleaseSignature.IsProtectedBinary(entry.FullName)) continue;
                using var contents = entry.Open();
                if (!binaries.TryAdd(entry.FullName,
                        Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant()))
                    throw new InvalidDataException($"ZIP 包含重复文件：{entry.FullName}");
            }
        }
        if (binaries.Count == 0) throw new InvalidDataException("ZIP 里没有任何可执行文件。");
        return (size, sha256, binaries);
    }

    private static ZipArchive OpenArchive(Stream stream)
    {
        try { return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new InvalidDataException("ZIP 文件无法读取或格式无效。"); }
    }

    /// <summary>The layout rules the loader relies on. A signed package has to satisfy them as
    /// uploaded: there is no repair step any more, because repairing bytes would break the
    /// signature. Each message says what to do, since the fix is always "repack with the tool".</summary>
    public static void Layout(string packagePath, string executable)
    {
        // Both handles are disposed before the caller moves the staging directory: a leaked stream
        // keeps package.zip open and Directory.Move then fails with a sharing violation.
        using var file = File.OpenRead(packagePath);
        using var archive = OpenArchive(file);
        if (archive.Entries.Count > 20_000)
            throw new InvalidDataException("ZIP 内的文件数量过多（上限 20000 个）。");
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if (name.Length == 0 || name.Length > 512 || name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
                throw new InvalidDataException($"ZIP 包含不安全或无效的文件路径：{name}");
            if (IsJunk(name))
                throw new InvalidDataException(
                    $"ZIP 包含系统垃圾文件（{name}）。请用 chispa-sign pack 重新打包，服务器不再改写已签名的包。");
            if (!seen.Add(name))
                throw new InvalidDataException($"ZIP 包含重复的文件路径：{name}");
            expanded += entry.Length;
            if (expanded > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("ZIP 解压后的内容过大（上限 4 GB）。");
            names.Add(name);
        }
        if (names.Count == 0) throw new InvalidDataException("ZIP 里没有文件，请重新打包。");
        if (!names.Any(name => name.Equals(executable, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                $"ZIP 顶层必须包含 {executable}。请用 chispa-sign pack 打包，入口 EXE 要放在源目录顶层。" +
                TopLevelHint(names));
        if (CommonWrapper(names) is not null)
            throw new InvalidDataException(
                "ZIP 被套了一个外层文件夹。服务器不再自动去掉它（那会改变签名覆盖的字节），请用 chispa-sign pack 重新打包。");
    }

    private static bool IsJunk(string name) =>
        name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase);

    private static string? CommonWrapper(IReadOnlyList<string> names)
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
        return tops.Length == 0 ? "" : $"当前 ZIP 顶层是：{string.Join("、", tops)}。";
    }
}
