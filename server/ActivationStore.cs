using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

record ActivationKey(string Id, string Hash, string Hint, int Days, string AccessMode,
    string[] ProgramIds, bool BetaAccess, DateTimeOffset CreatedAt, DateTimeOffset? RedeemBefore,
    string State = "unused", string? UsedBy = null, DateTimeOffset? UsedAt = null);
record GeneratedActivation(string Code, ActivationKey Key);
record ActivationTransaction(List<UserRecord> Users, List<ActivationKey> Keys);

sealed partial class Store
{
    private string ActivationPath => Path.Combine(root, "activation-keys.json");
    private string ActivationJournal => Path.Combine(root, "activation-transaction.json");

    // A durable intent is installed before either file changes. Every store read and startup
    // replays it, so a crash between the two replacements cannot make a code reusable.
    private void RecoverActivationTransaction()
    {
        if (!File.Exists(ActivationJournal)) return;
        var transaction = JsonSerializer.Deserialize<ActivationTransaction>(File.ReadAllText(ActivationJournal), Json)
            ?? throw new InvalidDataException("Invalid activation transaction.");
        WriteAtomic(Path.Combine(root, "users.json"), transaction.Users);
        WriteAtomic(ActivationPath, transaction.Keys);
        File.Delete(ActivationJournal);
    }

    private void CommitActivation(List<UserRecord> users, List<ActivationKey> keys)
    {
        WriteAtomic(ActivationJournal, new ActivationTransaction(users, keys));
        RecoverActivationTransaction();
    }

    public IReadOnlyList<ActivationKey> ListActivationKeys()
    {
        lock (gate) return (Read<List<ActivationKey>>(ActivationPath) ?? [])
            .OrderByDescending(k => k.CreatedAt).ToArray();
    }

    public IReadOnlyList<GeneratedActivation> GenerateActivationKeys(int count, int days, string accessMode,
        IEnumerable<string>? programIds, bool betaAccess, DateTimeOffset? redeemBefore)
    {
        if (count is < 1 or > 100) throw new ArgumentException("每批可生成 1 至 100 个激活码。");
        if (days is not (0 or 1 or 7 or 30 or 90 or 365)) throw new ArgumentException("请选择有效的授权时长。");
        if (accessMode == "none") throw new ArgumentException("激活码必须包含程序权限。");
        if (redeemBefore <= DateTimeOffset.UtcNow) throw new ArgumentException("兑换截止时间必须晚于现在。");
        lock (gate)
        {
            var ids = accessMode == "beta" ? [] : ValidateAccess(accessMode, programIds);
            if (accessMode == "beta") { betaAccess = true; days = 0; }
            var keys = Read<List<ActivationKey>>(ActivationPath) ?? [];
            var generated = new List<GeneratedActivation>();
            for (var i = 0; i < count; i++)
            {
                var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
                var code = "CSP-" + string.Join("-", Enumerable.Range(0, 8).Select(n => raw.Substring(n * 5, 5)));
                var key = new ActivationKey(Guid.NewGuid().ToString("N"), ActivationHash(code), raw[^6..],
                    days, accessMode, ids, betaAccess, DateTimeOffset.UtcNow, redeemBefore);
                keys.Add(key);
                generated.Add(new(code, key));
            }
            WriteAtomic(ActivationPath, keys);
            return generated;
        }
    }

    public void RevokeActivationKey(string id)
    {
        lock (gate)
        {
            var keys = Read<List<ActivationKey>>(ActivationPath) ?? [];
            var index = keys.FindIndex(k => k.Id == id);
            if (index < 0 || keys[index].State != "unused") throw new ArgumentException("只能作废未使用的激活码。");
            keys[index] = keys[index] with { State = "revoked" };
            WriteAtomic(ActivationPath, keys);
        }
    }

    public static string RegistrationEmail(string? value)
    {
        var email = (value ?? "").Trim().ToLowerInvariant();
        // Use the common unquoted mailbox form. Slash-containing addresses cannot round-trip
        // through the existing account-management URL routes, so do not create those accounts.
        if (email.Length > 254 || email.Split('@')[0].Length > 64 ||
            !Regex.IsMatch(email, @"^[a-z0-9_%+-]+(?:\.[a-z0-9_%+-]+)*@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$") ||
            !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            throw new ArgumentException("请输入有效的邮箱地址，例如 name@example.com。");
        return email;
    }

    private static string ActivationHash(string? code)
    {
        var normalized = (code ?? "").Trim().ToUpperInvariant().Replace("-", "");
        if (!Regex.IsMatch(normalized, "^CSP[0-9A-F]{40}$")) throw new ArgumentException("激活码格式不正确。");
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(normalized)));
    }

    private static int FindRedeemable(List<ActivationKey> keys, string? code)
    {
        var hash = ActivationHash(code);
        var index = keys.FindIndex(k => k.Hash == hash);
        if (index < 0 || keys[index].State != "unused" || keys[index].RedeemBefore <= DateTimeOffset.UtcNow)
            throw new ArgumentException("激活码不存在、已使用、已作废或已超过兑换期限。");
        return index;
    }

    public UserRecord RegisterAccount(string? email, string? password, string? code)
    {
        var account = RegistrationEmail(email);
        if (password is null || password.Length is < 12 or > 128)
            throw new ArgumentException("密码长度需要为 12 至 128 个字符。");
        lock (gate)
        {
            var users = Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [];
            if (users.Any(u => u.Username.Equals(account, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("该邮箱已注册，请登录后兑换激活码续费。");
            var keys = Read<List<ActivationKey>>(ActivationPath) ?? [];
            var index = FindRedeemable(keys, code);
            var key = keys[index];
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 310_000, HashAlgorithmName.SHA256, 32);
            var now = DateTimeOffset.UtcNow;
            var user = ApplyActivation(new UserRecord(account, Convert.ToBase64String(salt), Convert.ToBase64String(hash),
                now, "none", [], ProgramExpiries: new()), key, now);
            users.Add(user);
            keys[index] = key with { State = "used", UsedBy = account, UsedAt = now };
            CommitActivation(users, keys);
            return user;
        }
    }

    public UserRecord RedeemActivation(string username, string userHash, string? code)
    {
        lock (gate)
        {
            var users = Read<List<UserRecord>>(Path.Combine(root, "users.json")) ?? [];
            var userIndex = users.FindIndex(u => u.Username == username && u.Hash == userHash);
            if (userIndex < 0) throw new ArgumentException("登录已失效，请重新登录。");
            var user = users[userIndex];
            var keys = Read<List<ActivationKey>>(ActivationPath) ?? [];
            var index = FindRedeemable(keys, code);
            var key = keys[index];
            var now = DateTimeOffset.UtcNow;
            user = ApplyActivation(user, key, now);
            users[userIndex] = user;
            keys[index] = key with { State = "used", UsedBy = username, UsedAt = now };
            CommitActivation(users, keys);
            return user;
        }
    }
}
