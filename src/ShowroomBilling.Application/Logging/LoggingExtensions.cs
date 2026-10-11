using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace ShowroomBilling.Application.Logging;

public static class LoggingExtensions
{
    // Lines waiting for the writer thread. A full queue drops the line:
    // logging must never block the caller.
    private const int QueuedLineLimit = 4096;

    public static ILoggingBuilder AddRollingFile(
        this ILoggingBuilder builder,
        IConfiguration configuration,
        string filePrefix)
    {
        var section = configuration.GetSection(RollingFileLoggerOptions.SectionName);
        builder.Services.AddOptions<RollingFileLoggerOptions>()
            .Configure(options =>
            {
                section.Bind(options);
                if (string.IsNullOrWhiteSpace(options.FilePrefix) || options.FilePrefix == RollingFileLoggerOptions.DefaultFilePrefix)
                {
                    options.FilePrefix = filePrefix;
                }
                if (string.IsNullOrWhiteSpace(options.Directory))
                {
                    options.Directory = RollingFileLoggerOptions.DefaultDirectory();
                }
            });

        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            CreateRollingFileProvider(sp.GetRequiredService<IOptions<RollingFileLoggerOptions>>().Value));

        return builder;
    }

    /// <summary>
    /// The file logger itself: Serilog's rolling file sink behind a bounded
    /// queue, writing <see cref="LogLineFormatter"/> lines to
    /// <c>{prefix}-{yyyyMMdd}.log</c>. Disposing the provider writes out what
    /// is still queued.
    /// </summary>
    public static ILoggerProvider CreateRollingFileProvider(RollingFileLoggerOptions options)
    {
        var minLevel = ParseLevel(options.MinLevel);
        if (!options.Enabled || minLevel == LogLevel.None)
        {
            return NullLoggerProvider.Instance;
        }

        var path = Path.Combine(options.Directory, $"{options.FilePrefix}-.log");
        var sizeLimitBytes = Math.Max(1, options.FileSizeLimitMB) * 1024L * 1024L;
        TimeSpan? retention = options.RetentionDays > 0 ? TimeSpan.FromDays(options.RetentionDays) : null;

        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(minLevel))
            .WriteTo.Async(
                sink => sink.File(
                    new LogLineFormatter(),
                    path,
                    fileSizeLimitBytes: sizeLimitBytes,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    // Files are kept by age only; the sink's default would also cap them at 31.
                    retainedFileCountLimit: null,
                    retainedFileTimeLimit: retention),
                bufferSize: QueuedLineLimit,
                blockWhenFull: false)
            .CreateLogger();

        return new SerilogLoggerProvider(logger, dispose: true);
    }

    private static LogLevel ParseLevel(string value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level : LogLevel.Information;

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal
    };
}
