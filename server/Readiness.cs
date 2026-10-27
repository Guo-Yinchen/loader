using System.Text.Json;

namespace Chispa.Deployment;

// Readiness for an external checker, answered without telling the internet anything about this
// machine. The body is either {"status":"ready"} or {"status":"not-ready"} and nothing else - no
// versions, paths, counts or reasons. The reason goes to the log, where the operator already is.
//
// The checks are the ones that actually stop the service from doing its job: the data directory it
// must write to, and the account file it reads on every authenticated request. They run at most
// once per `cacheFor` window, because a checker polling every few seconds must not turn a health
// probe into disk load.
sealed class Readiness(string dataRoot, ILogger logger, TimeSpan? cacheFor = null)
{
    private readonly TimeSpan window = cacheFor ?? TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private DateTimeOffset nextCheck;
    private string? failure;

    public bool IsReady()
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow < nextCheck) return failure is null;
            nextCheck = DateTimeOffset.UtcNow + window;
            var previous = failure;
            failure = Probe();
            if (failure != previous)
            {
                if (failure is null) logger.LogInformation("readiness: ready");
                else logger.LogWarning("readiness: not ready - {Reason}", failure);
            }
            return failure is null;
        }
    }

    private string? Probe()
    {
        try
        {
            if (!Directory.Exists(dataRoot)) return "data directory is missing";
            // Writable, not just present: a full or read-only volume keeps serving stale data.
            var probe = Path.Combine(dataRoot, ".readiness-probe");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "data directory is not writable";
        }
        var users = Path.Combine(dataRoot, "users.json");
        try
        {
            if (File.Exists(users))
            {
                // Corrupt account data means every login fails; better to report not-ready than
                // to answer 401 to everyone and look healthy from the outside.
                using var document = JsonDocument.Parse(File.ReadAllText(users));
                if (document.RootElement.ValueKind != JsonValueKind.Array) return "account file is not an array";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return "account file is unreadable";
        }
        return null;
    }
}
