// Null ProgramExpiries identifies legacy accounts. Their scope and deadline are preserved
// until the first redemption; conversion is committed with the consumed code in one journal.
sealed partial class Store
{
    private static bool HasActiveSubscription(UserRecord user) => user.ProgramExpiries is { } expiries
        ? expiries.Values.Any(expiry => expiry is null || expiry > DateTimeOffset.UtcNow)
        : user.ExpiresAt is null || user.ExpiresAt > DateTimeOffset.UtcNow;

    public DateTimeOffset? ProgramExpiry(UserRecord user, string programId)
    {
        var program = GetProgram(programId);
        var scope = program?.IsBeta == true ? program.ParentProgramId ?? programId : programId;
        return user.ProgramExpiries is { } expiries
            ? expiries.GetValueOrDefault(scope, DateTimeOffset.MinValue) : user.ExpiresAt;
    }

    private Dictionary<string, DateTimeOffset?> Entitlements(UserRecord user) =>
        user.ProgramExpiries is { } expiries ? new(expiries, StringComparer.Ordinal) :
        ListAllPrograms().Where(p => !p.IsBeta && HasProgramScope(user, p.Id))
            .ToDictionary(p => p.Id, _ => user.ExpiresAt, StringComparer.Ordinal);

    private static UserRecord WithEntitlements(UserRecord user, Dictionary<string, DateTimeOffset?> expiries)
    {
        var ids = expiries.Keys.Order(StringComparer.Ordinal).ToArray();
        // The aggregate deadline is for older clients/account overview only. Authorization
        // always uses the individual program's deadline (including at ticket/heartbeat time).
        var aggregate = expiries.Count == 0 ? DateTimeOffset.MinValue :
            expiries.Values.Any(e => e is null) ? (DateTimeOffset?)null : expiries.Values.Max();
        return user with { ProgramExpiries = expiries, ProgramIds = ids, ExpiresAt = aggregate,
            AccessMode = ids.Length == 0 ? "none" : ids.Length == 1 ? "single" : "multiple" };
    }

    private UserRecord ApplyActivation(UserRecord user, ActivationKey key, DateTimeOffset now)
    {
        if (key.AccessMode != "beta") ValidateAccess(key.AccessMode, key.ProgramIds);
        var expiries = Entitlements(user);
        var targets = key.AccessMode == "beta" ? Array.Empty<string>() : key.AccessMode == "all"
            ? ListAllPrograms().Where(p => !p.IsBeta).Select(p => p.Id).ToArray() : key.ProgramIds;
        if (key.AccessMode == "all" && targets.Length == 0)
            throw new ArgumentException("尚未登记稳定程序，激活码未被使用。");
        var changed = (key.BetaAccess || key.AccessMode == "beta") && !(user.BetaAccess || user.AccessMode == "beta");
        foreach (var id in targets)
        {
            var exists = expiries.TryGetValue(id, out var expiry);
            if (exists && expiry is null) continue; // Never reduce a permanent entitlement.
            expiries[id] = key.Days == 0 ? null : (exists && expiry > now ? expiry.Value : now).AddDays(key.Days);
            changed = true;
        }
        if (!changed) throw new ArgumentException("这些程序已是永久授权，或已拥有 Beta 通行证；激活码未被使用。");
        return WithEntitlements(user with { BetaAccess = user.BetaAccess || user.AccessMode == "beta" ||
            key.BetaAccess || key.AccessMode == "beta" }, expiries);
    }

    public void SetProgramExpiry(string username, string programId, DateTimeOffset? expiry)
    {
        lock (gate)
        {
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            var index = users.FindIndex(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new ArgumentException("账号不存在。");
            var expiries = Entitlements(users[index]);
            if (!expiries.ContainsKey(programId)) throw new ArgumentException("账号没有此程序的授权。");
            expiries[programId] = expiry;
            users[index] = WithEntitlements(users[index], expiries);
            WriteAtomic(path, users);
        }
    }
}
