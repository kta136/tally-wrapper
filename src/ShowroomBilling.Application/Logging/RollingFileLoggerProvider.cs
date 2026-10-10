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

    // How long written lines may sit in the buffer when no further line arrives.
    private static readonly TimeSpan IdleFlushInterval = TimeSpan.FromSeconds(1);

    private DateOnly _currentDay;
    private StreamWriter? _writer;
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

        // A full queue drops the line: logging must never block the caller.
        _ = _queue.TryAdd(line);
    }

    private void WriterLoop()
    {
        try
        {
            while (!_queue.IsCompleted)
            {
                // Wait a bounded time for the next line. Without the timeout
                // the last lines before a quiet spell stay in the buffer, and
                // they are lost if the process is then killed.
                if (_queue.TryTake(out var line, (int)IdleFlushInterval.TotalMilliseconds, _stopCts.Token))
                {
                    WriteLine(line);
                }
                else
                {
                    FlushPending();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Drain();
        }
    }

    private void Drain()
    {
        while (_queue.TryTake(out var line))
        {
            WriteLine(line);
        }
        FlushPending();
    }

    private void FlushPending()
    {
        lock (_fileLock)
        {
            if (_pendingFlushWrites == 0)
            {
                return;
            }

            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Same policy as a failed write: give up on these lines and
                // let the next one reopen the file.
            }
            _pendingFlushWrites = 0;
            _lastFlushAt = DateTimeOffset.Now;
        }
    }

    private void WriteLine(string line)
    {
        lock (_fileLock)
        {
            try
            {
                RollIfNeeded();
                _writer!.WriteLine(line);
                _pendingFlushWrites++;
                if (_pendingFlushWrites >= 32 || DateTimeOffset.Now - _lastFlushAt >= IdleFlushInterval)
                {
                    _writer.Flush();
                    _pendingFlushWrites = 0;
                    _lastFlushAt = DateTimeOffset.Now;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Antivirus, a tail tool or a second instance briefly holding
                // the file must not stop logging for the rest of the session:
                // drop this line and reopen on the next one.
                try
                {
                    _writer?.Dispose();
                }
                catch
                {
                }
                _writer = null;
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
        var path = Path.Combine(_options.Directory, name);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
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
