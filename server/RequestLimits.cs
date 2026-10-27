using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http.Features;

namespace Chispa.Limits;

// Request budgets for the three kinds of traffic this service has: login, ordinary API calls and
// uploads. Each bucket is per address (and per account where an account is known), so one noisy or
// hostile client slows itself down instead of everyone else.
//
// The numbers are deliberately generous for a service used by a few dozen people: they stop floods
// and credential hammering, they are not a capacity plan. Nothing here is a promise about how many
// concurrent downloads the machine can serve.
sealed class RequestLimits
{
    private sealed record Bucket(DateTimeOffset Until, int Count);

    private readonly ConcurrentDictionary<string, Bucket> login = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bucket> api = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Bucket> upload = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> activeUploads = new(StringComparer.Ordinal);
    private readonly ILogger logger;

    public int LoginPerMinute { get; } = 20;
    public int ApiPerMinute { get; } = 900;
    public int UploadsPerMinute { get; } = 600;
    public int UploadConcurrency { get; } = 2;
    public long LoginBodyBytes { get; } = 8 * 1024;
    public long UploadBodyBytes { get; } = 64L * 1024 * 1024;   // one chunk, not the whole package

    public RequestLimits(ILogger logger)
    {
        this.logger = logger;
        LoginPerMinute = Bound("CHISPA_LIMIT_LOGIN_PER_MINUTE", LoginPerMinute, 5, 600);
        ApiPerMinute = Bound("CHISPA_LIMIT_API_PER_MINUTE", ApiPerMinute, 60, 100_000);
        UploadsPerMinute = Bound("CHISPA_LIMIT_UPLOADS_PER_MINUTE", UploadsPerMinute, 5, 5_000);
        UploadConcurrency = Bound("CHISPA_LIMIT_UPLOAD_CONCURRENCY", UploadConcurrency, 1, 16);
    }

    private static int Bound(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
            ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Key(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool Take(ConcurrentDictionary<string, Bucket> buckets, string key, int limit,
        TimeSpan window, DateTimeOffset now)
    {
        var updated = buckets.AddOrUpdate(key,
            _ => new Bucket(now + window, 1),
            (_, old) => old.Until <= now ? new Bucket(now + window, 1) : old with { Count = old.Count + 1 });
        return updated.Count <= limit;
    }

    /// <summary>Returns 429 when over budget, and also caps the login body so a huge payload cannot
    /// be parsed before the size is known to be sane.</summary>
    public bool TryEnter(HttpContext context, out int status)
    {
        status = StatusCodes.Status200OK;
        var path = context.Request.Path;
        var now = DateTimeOffset.UtcNow;
        var key = Key(context);
        // Small JSON/form endpoints must not inherit the legacy whole-ZIP upload limit.
        var bodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false } && !path.StartsWithSegments("/admin/releases"))
            bodyFeature.MaxRequestBodySize = 512 * 1024;
        if (path.StartsWithSegments("/api/v1/session") || path.StartsWithSegments("/api/v1/register") ||
            path.StartsWithSegments("/api/v1/activation") || path.StartsWithSegments("/admin/login"))
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = LoginBodyBytes;
            if (!Take(login, key, LoginPerMinute, TimeSpan.FromMinutes(1), now))
            {
                logger.LogWarning("login rate limit hit for {Address}", key);
                status = StatusCodes.Status429TooManyRequests;
                return false;
            }
            return true;
        }
        if (path.StartsWithSegments("/admin/uploads") && !HttpMethods.IsGet(context.Request.Method))
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = UploadBodyBytes;
            if (!Take(upload, key, UploadsPerMinute, TimeSpan.FromMinutes(1), now))
            {
                status = StatusCodes.Status429TooManyRequests;
                return false;
            }
            // Bounded concurrency per address: a broken client cannot hold every upload slot.
            var active = activeUploads.AddOrUpdate(key, 1, (_, old) => old + 1);
            if (active > UploadConcurrency)
            {
                activeUploads.AddOrUpdate(key, 0, (_, old) => Math.Max(0, old - 1));
                status = StatusCodes.Status429TooManyRequests;
                return false;
            }
            return true;
        }
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/admin/uploads"))
        {
            if (!Take(api, key, ApiPerMinute, TimeSpan.FromMinutes(1), now))
            {
                status = StatusCodes.Status429TooManyRequests;
                return false;
            }
        }
        return true;
    }

    public void Leave(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin/uploads") || HttpMethods.IsGet(context.Request.Method)) return;
        var key = Key(context);
        activeUploads.AddOrUpdate(key, 0, (_, old) => Math.Max(0, old - 1));
    }

    /// <summary>Drops buckets whose window has passed. Called from the maintenance timer so the
    /// dictionaries cannot grow with every address that ever connected.</summary>
    public void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, bucket) in login) if (bucket.Until <= now) login.TryRemove(key, out _);
        foreach (var (key, bucket) in api) if (bucket.Until <= now) api.TryRemove(key, out _);
        foreach (var (key, bucket) in upload) if (bucket.Until <= now) upload.TryRemove(key, out _);
        foreach (var (key, count) in activeUploads)
            if (count <= 0) activeUploads.TryRemove(new KeyValuePair<string, int>(key, count));
    }
}
