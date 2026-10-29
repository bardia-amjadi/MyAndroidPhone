using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using MyAndroidPhone.Core.Logging;

namespace MyAndroidPhone.Infrastructure.Logging;

public sealed class RollingFileLoggerOptions
{
    public required string Directory { get; init; }

    public string FileNamePrefix { get; init; } = "myandroidphone";

    public long MaxFileSizeBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Total number of log files kept, including the current one.</summary>
    public int RetainedFileCount { get; init; } = 5;

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
}

/// <summary>
/// Lightweight size-rotating file logger. Writes happen on one background thread so logging never blocks the UI.
/// Every line is passed through <see cref="LogRedactor"/>.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider, IRecentLogSource
{
    private const int RecentLineLimit = 500;

    private readonly RollingFileLoggerOptions _options;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly Task _writer;
    private int _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options;
        System.IO.Directory.CreateDirectory(options.Directory);
        _writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public string CurrentLogPath => Path.Combine(_options.Directory, _options.FileNamePrefix + ".log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public IReadOnlyList<string> GetRecentLines() => _recent.ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // The writer already reported what it could; shutdown must not throw.
        }
    }

    private void Enqueue(string line)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var redacted = LogRedactor.Redact(line);
        _recent.Enqueue(redacted);
        while (_recent.Count > RecentLineLimit && _recent.TryDequeue(out _))
        {
        }

        try
        {
            _queue.TryAdd(redacted);
        }
        catch (InvalidOperationException)
        {
            // Queue completed during shutdown.
        }
    }

    private void WriteLoop()
    {
        var batch = new StringBuilder();
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            batch.Clear();
            batch.AppendLine(first);
            var taken = 1;
            while (taken < 200 && _queue.TryTake(out var next))
            {
                batch.AppendLine(next);
                taken++;
            }

            try
            {
                RotateIfNeeded(batch.Length);
                File.AppendAllText(CurrentLogPath, batch.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never crash the application. The in-memory tail still has the lines.
            }
        }
    }

    private void RotateIfNeeded(int incomingChars)
    {
        var current = CurrentLogPath;
        if (!File.Exists(current) || new FileInfo(current).Length + incomingChars <= _options.MaxFileSizeBytes)
        {
            return;
        }

        var keep = Math.Max(1, _options.RetainedFileCount);
        if (keep == 1)
        {
            File.Delete(current);
            return;
        }

        string Rotated(int index) => Path.Combine(_options.Directory, $"{_options.FileNamePrefix}.{index}.log");

        var oldest = Rotated(keep - 1);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = keep - 2; i >= 1; i--)
        {
            if (File.Exists(Rotated(i)))
            {
                File.Move(Rotated(i), Rotated(i + 1));
            }
        }

        File.Move(current, Rotated(1));
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???"
    };

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider._options.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd'T'HH:mm:ss.fffzzz} [{Abbreviate(logLevel)}] {category}: {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            provider.Enqueue(line);
        }
    }
}
