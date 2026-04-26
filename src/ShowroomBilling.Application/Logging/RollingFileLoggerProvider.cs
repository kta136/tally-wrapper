using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShowroomBilling.Application.Logging;

[ProviderAlias("File")]
public sealed class RollingFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly RollingFileLoggerOptions _options;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _writerTask;
    private readonly object _fileLock = new();

    private DateOnly _currentDay;
    private StreamWriter? _writer;
    private string? _currentPath;
    private int _rollSuffix;
    private int _pendingFlushWrites;
    private DateTimeOffset _lastFlushAt = DateTimeOffset.Now;

    public RollingFileLoggerProvider(IOptions<RollingFileLoggerOptions> options)
        : this(options.Value)
    {
    }

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options;
        MinLevel = ParseLevel(options.MinLevel);
        ScopeProvider = new LoggerExternalScopeProvider();
        Directory.CreateDirectory(_options.Directory);
        _currentDay = DateOnly.FromDateTime(DateTime.Now);
        _writerTask = Task.Run(WriterLoop);
        try
        {
            PurgeOldFiles();
        }
        catch
        {
            // Retention is best-effort; never take down the process for it.
        }
    }

    internal IExternalScopeProvider ScopeProvider { get; private set; }

    internal LogLevel MinLevel { get; }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        ScopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();

    internal void Enqueue(string line)
    {
        if (!_options.Enabled)
        {
            return;
        }
        if (!_queue.TryAdd(line))
        {
            // Queue full — drop to keep logging non-blocking.
        }
    }

    private async Task WriterLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_stopCts.Token))
            {
                WriteLine(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await DrainAsync();
        }
    }

    private Task DrainAsync()
    {
        while (_queue.TryTake(out var line))
        {
            WriteLine(line);
        }
        lock (_fileLock)
        {
            _writer?.Flush();
        }
        return Task.CompletedTask;
    }

    private void WriteLine(string line)
    {
        lock (_fileLock)
        {
            RollIfNeeded();
            _writer!.WriteLine(line);
            _pendingFlushWrites++;
            if (_pendingFlushWrites >= 32 || DateTimeOffset.Now - _lastFlushAt >= TimeSpan.FromSeconds(1))
            {
                _writer.Flush();
                _pendingFlushWrites = 0;
                _lastFlushAt = DateTimeOffset.Now;
            }
        }
    }

    private void RollIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is null || today != _currentDay)
        {
            _currentDay = today;
            _rollSuffix = 0;
            OpenWriter();
            try
            {
                PurgeOldFiles();
            }
            catch
            {
            }
            return;
        }

        var limitBytes = Math.Max(1, _options.FileSizeLimitMB) * 1024L * 1024L;
        if (_writer.BaseStream.Length >= limitBytes)
        {
            _rollSuffix++;
            OpenWriter();
        }
    }

    private void OpenWriter()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch
        {
        }

        Directory.CreateDirectory(_options.Directory);
        var date = _currentDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var name = _rollSuffix == 0
            ? $"{_options.FilePrefix}-{date}.log"
            : $"{_options.FilePrefix}-{date}-{_rollSuffix}.log";
        _currentPath = Path.Combine(_options.Directory, name);
        var stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = false };
        _pendingFlushWrites = 0;
        _lastFlushAt = DateTimeOffset.Now;
    }

    private void PurgeOldFiles()
    {
        if (_options.RetentionDays <= 0 || !Directory.Exists(_options.Directory))
        {
            return;
        }
        var cutoff = DateTime.Now.AddDays(-_options.RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_options.Directory, $"{_options.FilePrefix}-*.log"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                }
            }
            catch
            {
                // Ignore per-file delete failures.
            }
        }
    }

    private static LogLevel ParseLevel(string value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Information;

    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
            _stopCts.Cancel();
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        lock (_fileLock)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
            }
            _writer = null;
        }
        _stopCts.Dispose();
        _queue.Dispose();
    }
}
