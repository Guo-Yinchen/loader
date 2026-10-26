using System.Collections.Concurrent;
using System.Diagnostics;

sealed class ServerMetrics
{
    private readonly ConcurrentDictionary<long, long> minuteRequests = new();
    private long requests;
    private long apiRequests;
    private long failedRequests;
    private long loginSuccess;
    private long loginFailure;
    private long downloads;
    private long clientProbes;
    private readonly object probeGate = new();
    private DateTimeOffset? lastClientProbeAt;
    private string? lastClientProbeAddress;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public void Record(PathString path, int status)
    {
        Interlocked.Increment(ref requests);
        if (path.StartsWithSegments("/api")) Interlocked.Increment(ref apiRequests);
        if (status >= 400) Interlocked.Increment(ref failedRequests);
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        minuteRequests.AddOrUpdate(minute, 1, (_, count) => count + 1);
    }

    public void Login(bool success)
    {
        if (success) Interlocked.Increment(ref loginSuccess);
        else Interlocked.Increment(ref loginFailure);
    }

    public void Download() => Interlocked.Increment(ref downloads);

    public void ClientProbe(string address)
    {
        Interlocked.Increment(ref clientProbes);
        lock (probeGate)
        {
            lastClientProbeAt = DateTimeOffset.UtcNow;
            lastClientProbeAddress = address;
        }
    }

    public MetricsSnapshot Snapshot(int activeSessions)
    {
        var currentMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        foreach (var key in minuteRequests.Keys.Where(key => key < currentMinute - 59))
            minuteRequests.TryRemove(key, out _);
        var history = Enumerable.Range(0, 60)
            .Select(offset => minuteRequests.GetValueOrDefault(currentMinute - 59 + offset))
            .ToArray();
        using var process = Process.GetCurrentProcess();
        DateTimeOffset? probeAt;
        string? probeAddress;
        lock (probeGate) { probeAt = lastClientProbeAt; probeAddress = lastClientProbeAddress; }
        return new MetricsSnapshot(startedAt, (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            Interlocked.Read(ref requests),
            Interlocked.Read(ref apiRequests), Interlocked.Read(ref failedRequests),
            Interlocked.Read(ref loginSuccess), Interlocked.Read(ref loginFailure),
            Interlocked.Read(ref downloads), activeSessions, process.WorkingSet64, history,
            Interlocked.Read(ref clientProbes), probeAt, probeAddress);
    }
}

record MetricsSnapshot(DateTimeOffset StartedAt, long UptimeSeconds, long Requests, long ApiRequests,
    long FailedRequests, long LoginSuccess, long LoginFailure, long Downloads,
    int ActiveSessions, long MemoryBytes, long[] LastHour, long ClientProbes,
    DateTimeOffset? LastClientProbeAt, string? LastClientProbeAddress);
