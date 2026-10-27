using System.Text;

namespace Chispa.Deployment;

// A small rotating file log, because a service that runs for months unattended cannot write to a
// console nobody is watching and must not let its log grow without limit.
//
// Layout: <directory>\chispa-YYYYMMDD.log, and when a file passes its size cap it is rolled to
// chispa-YYYYMMDD.001.log, .002, ... rather than truncated, so the seconds before a failure are
// still on disk. Files older than the retention window are pruned when the date changes.
//
// Deliberately not a logging platform: no network sink, no database, no background thread that
// could block shutdown. A line is written under a lock and appended; anything that goes wrong is
// swallowed, because a logger that throws is worse than a logger that drops a line.
sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly string directory;
    private readonly int keepDays;
    private readonly long maxBytes;
    private readonly object gate = new();
    private DateOnly prunedFor;

    public RotatingFileLoggerProvider(string directory, int keepDays = 14, long maxBytes = 32L * 1024 * 1024)
    {
        this.directory = directory;
        this.keepDays = Math.Max(1, keepDays);
        this.maxBytes = Math.Max(1024 * 1024, maxBytes);
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, Short(categoryName));

    public void Dispose() { }

    private static string Short(string category)
    {
        // "Microsoft.AspNetCore.Hosting.Diagnostics" -> "AspNetCore.Hosting.Diagnostics"
        var trimmed = category.Replace("Microsoft.", "", StringComparison.Ordinal).Replace("Chispa.", "", StringComparison.Ordinal);
        return trimmed.Length <= 60 ? trimmed : trimmed[..60];
    }

    private void Write(LogLevel level, string category, string message)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder(message.Length + 80)
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ').Append(Level(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message)
            .AppendLine()
            .ToString();
        lock (gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.DateTime);
                var path = Path.Combine(directory, $"chispa-{today:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes) Roll(path);
                File.AppendAllText(path, line, Encoding.UTF8);
                if (prunedFor != today)
                {
                    prunedFor = today;
                    Prune(today);
                }
            }
            catch
            {
                // Never let logging take the service down or fail a request.
            }
        }
    }

    private static void Roll(string path)
    {
        for (var index = 1; index < 1000; index++)
        {
            var rolled = Path.ChangeExtension(path, null) is { } stem ? $"{stem}.{index:000}.log" : $"{path}.{index:000}";
            if (File.Exists(rolled)) continue;
            File.Move(path, rolled);
            return;
        }
    }

    private void Prune(DateOnly today)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "chispa-*.log"))
            {
                var info = new FileInfo(file);
                if (DateOnly.FromDateTime(info.LastWriteTime).DayNumber < today.DayNumber - keepDays)
                    info.Delete();
            }
        }
        catch
        {
            // Retention is housekeeping; never fail a write because of it.
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "----",
    };

    private sealed class FileLogger(RotatingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += Environment.NewLine + exception;
            provider.Write(logLevel, category, message);
        }
    }
}
