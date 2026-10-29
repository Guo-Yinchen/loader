// Deadlines stay in wall-clock UTC. Each account remembers the program's pause offset
// at the last edit, so renewals during a pause never receive earlier paused time twice.
sealed partial class Store
{
    private long BillingOffset(string id, DateTimeOffset now)
    {
        var program = GetProgram(id);
        if (program is null) return 0;
        return program.BillingPausedTicks + (program.BillingPausedAt is { } start
            ? Math.Max(0, (now - start).Ticks) : 0);
    }

    private Dictionary<string, long> BillingOffsets(IEnumerable<string> ids) =>
        ids.ToDictionary(id => id, id => BillingOffset(id, DateTimeOffset.UtcNow));

    private DateTimeOffset? EffectiveExpiry(UserRecord user, string id, DateTimeOffset? expiry)
    {
        if (expiry is null || expiry == DateTimeOffset.MinValue) return expiry;
        var delta = BillingOffset(id, DateTimeOffset.UtcNow) - (user.BillingOffsets?.GetValueOrDefault(id) ?? 0);
        return new DateTimeOffset(Math.Clamp(expiry.Value.UtcTicks + delta,
            DateTimeOffset.MinValue.Ticks, DateTimeOffset.MaxValue.Ticks), TimeSpan.Zero);
    }

    public Dictionary<string, DateTimeOffset?> EffectiveEntitlements(UserRecord user)
    {
        lock (gate) return Entitlements(user);
    }

    public DateTimeOffset? AccountExpiry(UserRecord user)
    {
        lock (gate)
        {
            var values = Entitlements(user).Values;
            return values.Count == 0 ? user.ExpiresAt : values.Any(e => e is null) ? null : values.Max();
        }
    }

    public void SetBillingPaused(string id, bool paused)
    {
        lock (gate)
        {
            var program = GetProgram(id) ?? throw new ArgumentException("程序不存在。");
            if (program.IsBeta) throw new ArgumentException("Beta 与所属稳定程序共用计费，请操作稳定程序。");
            if (paused == (program.BillingPausedAt is not null)) return;
            var now = DateTimeOffset.UtcNow;
            WriteAtomic(Path.Combine(root, "programs", id, "program.json"), program with
            {
                BillingPausedTicks = BillingOffset(id, now), BillingPausedAt = paused ? now : null
            });
        }
    }

    public int CompensateProgram(string id, int minutes)
    {
        if (minutes is < 1 or > 525600) throw new ArgumentException("补偿时长必须为 1 至 525600 分钟。");
        lock (gate)
        {
            var program = GetProgram(id) ?? throw new ArgumentException("程序不存在。");
            if (program.IsBeta) throw new ArgumentException("请对所属稳定程序补偿，Beta 自动共享时长。");
            var path = Path.Combine(root, "users.json");
            var users = Read<List<UserRecord>>(path) ?? [];
            var count = 0;
            for (var i = 0; i < users.Count; i++)
            {
                var expiries = Entitlements(users[i]);
                if (!expiries.TryGetValue(id, out var expiry) || expiry is null || expiry <= DateTimeOffset.UtcNow) continue;
                try { expiries[id] = expiry.Value.AddMinutes(minutes); }
                catch (ArgumentOutOfRangeException) { throw new ArgumentException("补偿后到期时间超出允许范围，未修改任何账号。"); }
                users[i] = WithEntitlements(users[i], expiries);
                count++;
            }
            if (count > 0) WriteAtomic(path, users);
            return count;
        }
    }
}
