using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chispa.Release;

// Packaging and signing for Chispa releases. Runs in the release environment, next to the private
// key; the server and the loader only ever see the public half and the signature it produced.
//
//   chispa-sign keygen --out <dir> [--id <keyId>] [--password-file <f>]
//   chispa-sign pack   --source <folder> --out <zip> --executable <name.exe>
//   chispa-sign sign   --package <zip> --program <id> --version <x.y.z> --executable <name.exe>
//                      --key <private.pem> --key-id <id> --out <signature.json> [--password-file <f>]
//   chispa-sign verify --package <zip> --signature <sig.json> --trust <trust.json>
//
// The signing tool is also the packager on purpose: the signature covers the exact bytes of the
// ZIP, so the thing that decides those bytes has to be the thing that signs them. The server never
// repacks, never normalises and never re-signs an uploaded package.

return SigningTool.Run(args);

static class SigningTool
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "keygen" => Keygen(args),
                "pack" => Pack(args),
                "sign" => Sign(args),
                "verify" => Verify(args),
                "inspect-launcher" => LauncherInspection.Run(args),
                "--help" or "-h" or "/?" => Usage(0),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException
            or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }

    private static int Usage(int code = 2)
    {
        var text =
            """
            chispa-sign — 发布包打包与签名

              keygen --out <目录> [--id <密钥标识>] [--password-file <密码文件>]
                     生成 RSA-2048 私钥（PKCS#8 PEM）与公钥文件（JSON）。
                     私钥只留在发布环境；把打印出的 C# 片段贴进 client/ReleaseTrust.cs 后重新构建客户端。

              pack   --source <目录> --out <zip> --executable <入口.exe>
                     按发布要求打包：入口 EXE 在顶层、去掉系统垃圾文件、拒绝不安全路径。
                     签名覆盖这个 ZIP 的字节，所以请用本工具打包而不是手工压缩。

              sign   --package <zip> --program <程序ID> --version <x.y.z> --executable <入口.exe>
                     --key <私钥.pem> --key-id <密钥标识> --out <signature.json>
                     [--password-file <密码文件>]

              verify --package <zip> --signature <sig.json> --trust <trust.json>
                     用受信任公钥复核一个已签名的包，发布前自查用。

              inspect-launcher <exe> <expected-server-url>
                     读取管理员启动清单及单文件包内的程序集地址，无需启动 GUI 或提权。
            """;
        (code == 0 ? Console.Out : Console.Error).WriteLine(text);
        return code;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        return Usage();
    }

    // ---- keygen -------------------------------------------------------------------------------
    private static int Keygen(string[] args)
    {
        var keyId = Option(args, "--id") ?? $"release-{DateTime.UtcNow:yyyyMMdd}";
        var output = Required(args, "--out");
        var passwordFile = Option(args, "--password-file");
        Directory.CreateDirectory(output);
        var privatePath = Path.Combine(output, "release-private.pem");
        var publicPath = Path.Combine(output, "release-public.json");
        if (File.Exists(privatePath))
            throw new InvalidOperationException(
                $"私钥已存在：{privatePath}。确认要换密钥时先手工移走它（轮换需要重新分发客户端）。");

        using var rsa = RSA.Create(2048);
        string pem;
        if (passwordFile is null)
        {
            pem = rsa.ExportPkcs8PrivateKeyPem();
        }
        else
        {
            var password = ReadPasswordFile(passwordFile);
            pem = rsa.ExportEncryptedPkcs8PrivateKeyPem(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        }
        File.WriteAllText(privatePath, pem, new UTF8Encoding(false));
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        File.WriteAllText(publicPath,
            ReleaseTrustFile.Serialize([new TrustedReleaseKey(keyId, publicKey)]), new UTF8Encoding(false));

        Console.WriteLine($"私钥：{privatePath}（不要提交、不要复制到下载服务器）");
        Console.WriteLine($"公钥：{publicPath}");
        Console.WriteLine();
        Console.WriteLine("客户端信任锚（粘贴到 client/ReleaseTrust.cs 的 BuiltIn 数组）：");
        Console.WriteLine($"    new(\"{keyId}\", \"{publicKey}\"),");
        Console.WriteLine();
        Console.WriteLine("服务端发布校验（CHISPA_RELEASE_TRUST_KEYS 指向这个文件）：");
        Console.WriteLine($"    {publicPath}");
        return 0;
    }

    // ---- pack ---------------------------------------------------------------------------------
    private static int Pack(string[] args)
    {
        var source = Path.GetFullPath(Required(args, "--source"));
        var output = Path.GetFullPath(Required(args, "--out"));
        var executable = Required(args, "--executable");
        if (!Directory.Exists(source)) throw new InvalidDataException($"源目录不存在：{source}");
        if (!File.Exists(Path.Combine(source, executable)))
            throw new InvalidDataException($"源目录顶层缺少入口文件 {executable}");
        if (File.Exists(output)) File.Delete(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: Path.GetRelativePath(source, path).Replace('\\', '/')))
            .Where(file => !IsJunk(file.Name))
            // A trailing slash or a ".." segment here would produce a ZIP the loader must refuse:
            // fail now, in the release environment, rather than at the server or at the user.
            .Where(file => file.Name.Split('/').All(part => part is not ("" or "." or "..")))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) throw new InvalidDataException("源目录里没有可打包的文件。");

        using (var archive = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            foreach (var (path, name) in files)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(path);
                using var input = File.OpenRead(path);
                using var destination = entry.Open();
                input.CopyTo(destination);
            }
        }
        var info = new FileInfo(output);
        Console.WriteLine($"已打包 {files.Length} 个文件：{output}（{info.Length:N0} 字节）");
        Console.WriteLine($"SHA-256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant()}");
        return 0;
    }

    // ---- sign ---------------------------------------------------------------------------------
    private static int Sign(string[] args)
    {
        var package = Path.GetFullPath(Required(args, "--package"));
        var programId = Required(args, "--program");
        var version = Required(args, "--version");
        var executable = Required(args, "--executable");
        var keyPath = Path.GetFullPath(Required(args, "--key"));
        var keyId = Required(args, "--key-id");
        var output = Path.GetFullPath(Required(args, "--out"));
        var passwordFile = Option(args, "--password-file");

        using var rsa = LoadPrivateKey(keyPath, passwordFile);
        if (rsa.KeySize < 2048) throw new InvalidDataException("签名密钥至少需要 2048 位。");
        var fields = DescribePackage(package, programId, version, executable);
        var envelope = ReleaseSignature.Sign(fields, rsa, keyId);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, envelope, new UTF8Encoding(false));
        Console.WriteLine($"已签名 {programId} {version}：{output}");
        Console.WriteLine($"  keyId      {keyId}");
        Console.WriteLine($"  size       {fields.Size}");
        Console.WriteLine($"  sha256     {fields.Sha256}");
        Console.WriteLine($"  binaries   {fields.BinariesSha256}");
        // Self-check: the tool must be able to verify what it just produced, or the operator finds
        // out at upload time (or worse, the user finds out at install time).
        var keys = new[] { new TrustedReleaseKey(keyId, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())) };
        if (!ReleaseSignature.TryOpen(ReleaseSignature.ToTransport(envelope), keys, out var signed, out _, out var error))
            throw new InvalidDataException("刚刚生成的签名自检失败：" + error);
        if (!ReleaseSignature.Matches(signed!, fields.ProgramId, fields.Version, fields.Executable,
                fields.Size, fields.Sha256, out error))
            throw new InvalidDataException("刚刚生成的签名与包内容不一致：" + error);
        return 0;
    }

    // ---- verify -------------------------------------------------------------------------------
    private static int Verify(string[] args)
    {
        var package = Path.GetFullPath(Required(args, "--package"));
        var signaturePath = Path.GetFullPath(Required(args, "--signature"));
        var trustPath = Path.GetFullPath(Required(args, "--trust"));
        var keys = ReleaseTrustFile.Load(trustPath);
        var envelope = File.ReadAllText(signaturePath);
        if (!ReleaseSignature.TryOpen(ReleaseSignature.ToTransport(envelope), keys, out var signed,
                out var keyId, out var error))
        {
            Console.Error.WriteLine("校验失败：" + error);
            return 1;
        }
        // Re-derive size/hash/binaries from the package itself, so this checks the same thing the
        // server and the loader will: the bytes on disk, not the arguments of the signing command.
        var fields = DescribePackage(package, signed!.ProgramId, signed.Version, signed.Executable);
        if (!ReleaseSignature.Matches(signed, fields.ProgramId, fields.Version, fields.Executable,
                fields.Size, fields.Sha256, out error) ||
            !string.Equals(fields.BinariesSha256, signed.BinariesSha256, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("校验失败：签名与包内容不一致（" + error + "）");
            return 1;
        }
        Console.WriteLine($"签名有效：{signed.ProgramId} {signed.Version} keyId={keyId}");
        return 0;
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static SignedReleaseFields DescribePackage(string package, string programId, string version, string executable)
    {
        if (!File.Exists(package)) throw new InvalidDataException($"包不存在：{package}");
        if (!System.Text.RegularExpressions.Regex.IsMatch(programId, "^[a-z0-9][a-z0-9-]{1,31}$"))
            throw new ArgumentException("程序 ID 无效。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
            throw new ArgumentException("版本号格式应为 1.0.0。");
        if (string.IsNullOrWhiteSpace(executable) || executable != Path.GetFileName(executable))
            throw new ArgumentException("入口文件名无效。");

        var binaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = File.OpenRead(package);
        var sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (!archive.Entries.Any(entry => string.Equals(entry.FullName, executable, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"ZIP 顶层缺少入口文件 {executable}。");
            foreach (var entry in archive.Entries)
            {
                if (!ReleaseSignature.IsProtectedBinary(entry.FullName)) continue;
                using var contents = entry.Open();
                if (!binaries.TryAdd(entry.FullName, Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant()))
                    throw new InvalidDataException($"ZIP 包含重复文件：{entry.FullName}");
            }
        }
        if (binaries.Count == 0) throw new InvalidDataException("ZIP 里没有任何可执行文件。");
        return new SignedReleaseFields(programId, version, executable, stream.Length, sha,
            ReleaseSignature.BinariesDigest(binaries));
    }

    private static RSA LoadPrivateKey(string path, string? passwordFile)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"私钥不存在：{path}");
        var pem = File.ReadAllText(path);
        var rsa = RSA.Create();
        if (pem.Contains("ENCRYPTED", StringComparison.Ordinal))
        {
            if (passwordFile is null)
                throw new InvalidOperationException("私钥已加密，需要通过 --password-file 提供密码文件。");
            rsa.ImportFromEncryptedPem(pem, ReadPasswordFile(passwordFile));
        }
        else rsa.ImportFromPem(pem);
        return rsa;
    }

    private static string ReadPasswordFile(string path) => File.ReadAllText(path).Trim();

    private static bool IsJunk(string name) =>
        name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/.DS_Store", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("/desktop.ini", StringComparison.OrdinalIgnoreCase);

    private static string Required(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"缺少参数 {name}");
        return args[index + 1];
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length) return null;
        return args[index + 1];
    }
}
