using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chispa.Release;

// The one definition of what a signed release is, shared by the three places that need it:
// the signing tool (produces), the server (verifies before publishing, so a broken signing step
// is caught before distribution) and the loader (verifies before installing and before launching).
//
// WHY A CANONICAL TEXT STATEMENT AND NOT "THE JSON WE SENT".
// A signature covers bytes, not meaning. If the statement were JSON, the client would have to
// re-serialize it to check the signature, and every serializer differs on property order,
// whitespace, number formatting, unicode escaping and duplicate keys. Two JSON writers that
// disagree by one space produce a valid document that no longer verifies (or, worse, a weaker
// check that normalises before comparing). So the statement is a fixed, line-oriented format:
// one field per line, fixed order, LF only, no escaping to get wrong, no optional fields. The
// client verifies the signature over exactly these bytes and then compares each parsed field
// with what the server claimed. There is nothing to canonicalise because there is only one
// spelling of every statement.
//
// FORMAT VERSION. The first line is the format tag. A future format is `chispa-release-v2`, and
// a client that only knows v1 rejects it instead of guessing - so a format change can never be
// silently downgraded.

/// <summary>Fields covered by a release signature. `BinariesSha256` binds the per-file hash map
/// the loader uses to verify an installed program, so a compromised server cannot supply hashes
/// that match files the attacker modified on the user's disk.</summary>
public sealed record SignedReleaseFields(string ProgramId, string Version, string Executable, long Size,
    string Sha256, string BinariesSha256);

/// <summary>A public key the loader is willing to trust. Only the id and the public half travel.</summary>
public sealed record TrustedReleaseKey(string KeyId, string PublicKeyBase64)
{
    public RSA CreateKey()
    {
        var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
        return rsa;
    }
}

public static class ReleaseStatement
{
    /// <summary>Format tag; part of the signed bytes and of the version negotiation.</summary>
    public const string Format = "chispa-release-v1";
    public const int FieldCount = 6;

    public static string Build(SignedReleaseFields fields) => string.Join('\n',
    [
        Format,
        "programId=" + fields.ProgramId,
        "version=" + fields.Version,
        "executable=" + fields.Executable,
        "size=" + fields.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "sha256=" + fields.Sha256,
        "binariesSha256=" + fields.BinariesSha256,
    ]);

    // Strict: exactly seven lines, exact order, exact tag, no trailing newline, no padding
    // tolerance. A statement that is not the one this client writes is not one it will trust,
    // even if the signature over it is valid - that is what stops a future format from being
    // accepted by an old client.
    public static bool TryParse(string text, out SignedReleaseFields? fields, out string error)
    {
        fields = null;
        var lines = text.Split('\n');
        if (lines.Length != FieldCount + 1) { error = "签名清单行数不正确"; return false; }
        if (lines[0] != Format) { error = $"不支持的签名清单格式：{lines[0]}"; return false; }

        string? Value(string prefix)
        {
            var line = lines[Array.IndexOf(Prefixes, prefix) + 1];
            return line.StartsWith(prefix!, StringComparison.Ordinal) ? line[prefix!.Length..] : null;
        }

        var programId = Value("programId=");
        var version = Value("version=");
        var executable = Value("executable=");
        var sizeText = Value("size=");
        var sha256 = Value("sha256=");
        var binaries = Value("binariesSha256=");
        if (programId is null || version is null || executable is null || sizeText is null ||
            sha256 is null || binaries is null)
        {
            error = "签名清单字段顺序或名称不正确";
            return false;
        }
        if (!long.TryParse(sizeText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size) || size <= 0)
        {
            error = "签名清单里的包大小无效";
            return false;
        }
        if (!IsSha256(sha256) || !IsSha256(binaries)) { error = "签名清单里的哈希格式无效"; return false; }
        if (programId.Length == 0 || version.Length == 0 || executable.Length == 0)
        {
            error = "签名清单缺少程序信息";
            return false;
        }
        fields = new SignedReleaseFields(programId, version, executable, size, sha256, binaries);
        error = "";
        return true;
    }

    private static readonly string[] Prefixes =
        ["programId=", "version=", "executable=", "size=", "sha256=", "binariesSha256="];

    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class ReleaseSignature
{
    public const string EnvelopeFormat = "chispa-release-signature-v1";
    // Named, not negotiated: if a weaker algorithm is ever added it gets its own name and old
    // clients keep rejecting it rather than falling back to whatever the envelope offers.
    public const string Algorithm = "RSA-2048-PKCS1-SHA256";
    public const string PrivateKeyIdPrefix = "release-";

    public sealed record Envelope(string Format, string KeyId, string Algorithm, string Statement, string Signature);

    private static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    /// <summary>Signs the canonical statement. Only the signing tool calls this, and only with a
    /// private key that never reaches the server or the loader.</summary>
    public static string Sign(SignedReleaseFields fields, RSA privateKey, string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)) throw new ArgumentException("key id is required");
        if (privateKey.KeySize < 2048) throw new ArgumentException("the signing key must be at least 2048 bits");
        var statement = ReleaseStatement.Build(fields);
        var signature = privateKey.SignData(Encoding.UTF8.GetBytes(statement), HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var envelope = new Envelope(EnvelopeFormat, keyId, Algorithm, statement, Convert.ToBase64String(signature));
        return JsonSerializer.Serialize(envelope, EnvelopeJson);
    }

    /// <summary>The transport form: the envelope JSON as base64. One opaque string in the API means
    /// the wire format of the manifest cannot drift from the bytes that were signed.</summary>
    public static string ToTransport(string envelopeJson) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(envelopeJson));

    public static string FromTransport(string envelopeBase64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(envelopeBase64));

    /// <summary>Verifies a transported envelope against the trusted keys and returns the fields it
    /// actually signed. Does NOT compare those fields with what the server said - the caller does
    /// that, because only the caller knows which program/version it asked for.</summary>
    public static bool TryOpen(string? envelopeBase64, IReadOnlyCollection<TrustedReleaseKey> keys,
        out SignedReleaseFields? fields, out string keyId, out string error)
    {
        fields = null;
        keyId = "";
        if (string.IsNullOrWhiteSpace(envelopeBase64))
        {
            error = "这个版本没有签名，已拒绝安装";
            return false;
        }
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(FromTransport(envelopeBase64), EnvelopeJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            error = "签名数据无法解析";
            return false;
        }
        if (envelope is null ||
            envelope.Format != EnvelopeFormat ||
            envelope.Algorithm != Algorithm ||
            string.IsNullOrEmpty(envelope.KeyId) ||
            string.IsNullOrEmpty(envelope.Statement) ||
            string.IsNullOrEmpty(envelope.Signature))
        {
            error = "签名格式或算法不受支持";
            return false;
        }
        var trusted = keys.FirstOrDefault(key => string.Equals(key.KeyId, envelope.KeyId, StringComparison.Ordinal));
        if (trusted is null)
        {
            // Unknown key is a refusal, not a warning: a rotation removes the old key from a later
            // build precisely so that signatures from it stop being accepted.
            error = $"签名密钥不受信任：{envelope.KeyId}";
            return false;
        }
        byte[] signature;
        try { signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { error = "签名数据无法解析"; return false; }

        byte[] signed = Encoding.UTF8.GetBytes(envelope.Statement);
        bool valid;
        try
        {
            using var rsa = trusted.CreateKey();
            valid = rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            error = $"签名密钥不可用：{envelope.KeyId}";
            return false;
        }
        if (!valid) { error = "签名校验失败，内容可能已被替换"; return false; }

        if (!ReleaseStatement.TryParse(envelope.Statement, out fields, out error)) return false;
        keyId = envelope.KeyId;
        return true;
    }

    /// <summary>Compares the verified statement with the manifest the client is about to act on.
    /// Every security-relevant field is compared, so a substituted manifest cannot ride on a
    /// signature that was issued for something else.</summary>
    public static bool Matches(SignedReleaseFields signed, string programId, string version, string executable,
        long size, string sha256, out string error)
    {
        if (!string.Equals(signed.ProgramId, programId, StringComparison.Ordinal))
        { error = "签名里的程序 ID 与清单不一致"; return false; }
        if (!string.Equals(signed.Version, version, StringComparison.Ordinal))
        { error = "签名里的版本与清单不一致"; return false; }
        if (!string.Equals(signed.Executable, executable, StringComparison.OrdinalIgnoreCase))
        { error = "签名里的入口文件与清单不一致"; return false; }
        if (signed.Size != size) { error = "签名里的包大小与清单不一致"; return false; }
        if (!string.Equals(signed.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        { error = "签名里的包哈希与清单不一致"; return false; }
        error = "";
        return true;
    }

    // ---- per-file hashes ---------------------------------------------------------------------
    // The map the loader checks installed files against. It is covered by `binariesSha256` in the
    // statement, so a compromised server cannot answer "yes, that modified DLL is the published
    // one": the digest of the map it serves has to equal the digest that was signed.

    public static string BinariesText(IReadOnlyDictionary<string, string> binaries) =>
        string.Join('\n', binaries
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value.ToLowerInvariant()));

    public static string BinariesDigest(IReadOnlyDictionary<string, string> binaries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BinariesText(binaries)))).ToLowerInvariant();

    public static Dictionary<string, string> CompareBinaries(SignedReleaseFields signed,
        IReadOnlyDictionary<string, string> binaries)
    {
        // Returns the map when it is the signed one, throws otherwise. Kept as a method so the
        // check cannot be forgotten in a caller: it is the difference between "the package is
        // signed" and "the files we are about to trust are the signed ones".
        var digest = BinariesDigest(binaries);
        if (!string.Equals(digest, signed.BinariesSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件清单与签名不一致，已拒绝安装或启动。");
        return new Dictionary<string, string>(binaries, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>True for the file types the loader verifies. Kept here so the signer, the server
    /// and the loader cannot disagree about which files a package's identity consists of.</summary>
    public static bool IsProtectedBinary(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".sys" or ".ocx" or ".com";
}

/// <summary>The operator-side list of trusted public keys: same file read by the server (to check
/// uploads) and by the signing tool (to check its own output). The loader carries the same keys
/// compiled in, because a client that reads its trust anchors from a file the server could write
/// would not be verifying anything.</summary>
public static class ReleaseTrustFile
{
    private sealed record KeyEntry(string KeyId, string PublicKey);

    private sealed record KeyDocument(KeyEntry[]? Keys);

    public static IReadOnlyList<TrustedReleaseKey> Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"trust key file not found: {path}", path);
        return Parse(File.ReadAllText(path));
    }

    public static IReadOnlyList<TrustedReleaseKey> Parse(string json)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Both spellings are accepted so a hand-written one-line list does not need a wrapper:
        //   [ {"keyId": "...", "publicKey": "..."} ]
        //   { "keys": [ ... ] }
        KeyEntry[]? entries = json.TrimStart().StartsWith('[')
            ? JsonSerializer.Deserialize<KeyEntry[]>(json, options)
            : JsonSerializer.Deserialize<KeyDocument>(json, options)?.Keys;
        var keys = entries?
            .Select(entry => new TrustedReleaseKey(entry.KeyId ?? "", entry.PublicKey ?? "")).ToArray();
        if (keys is null || keys.Length == 0)
            throw new InvalidDataException("trust key file contains no keys");
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId) || string.IsNullOrWhiteSpace(key.PublicKeyBase64))
                throw new InvalidDataException("trust key file has an empty key id or key");
            // Import once here: a malformed key must fail at configuration time, not at verification
            // time where it would look like a bad signature.
            using var rsa = key.CreateKey();
            if (rsa.KeySize < 2048) throw new InvalidDataException($"key {key.KeyId} is weaker than 2048 bits");
        }
        return keys;
    }

    public static string Serialize(IEnumerable<TrustedReleaseKey> keys) =>
        JsonSerializer.Serialize(new KeyDocument(keys
            .Select(key => new KeyEntry(key.KeyId, key.PublicKeyBase64)).ToArray()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
}
