using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Api.Configuration;

namespace ShowroomBilling.Tests;

/// <summary>
/// The installed server has no appsettings.json, so the log levels it runs at
/// are the ones the API sets for itself.
/// </summary>
public sealed class LogLevelDefaultsTests
{
    private const string FrameworkRequestLines = "Microsoft.AspNetCore.Hosting.Diagnostics";
    private const string OurOwnLines = "ShowroomBilling.Infrastructure.Bills.BillPostingWorkflow";

    [Fact]
    public void With_no_settings_file_the_frameworks_request_lines_are_off_and_ours_are_on()
    {
        var configuration = new ConfigurationManager();
        LogLevelDefaults.AddTo(configuration);

        using var loggers = LoggersFor(configuration);

        Assert.False(loggers.CreateLogger(FrameworkRequestLines).IsEnabled(LogLevel.Information));
        Assert.True(loggers.CreateLogger(FrameworkRequestLines).IsEnabled(LogLevel.Warning));
        Assert.True(loggers.CreateLogger(OurOwnLines).IsEnabled(LogLevel.Information));
        Assert.False(loggers.CreateLogger(OurOwnLines).IsEnabled(LogLevel.Debug));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_settings_file_or_environment_variable_still_changes_a_level(bool addedBeforeTheDefaults)
    {
        var configuration = new ConfigurationManager();
        var fromOutside = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
            ["Logging:LogLevel:Default"] = "Debug",
        };
        if (addedBeforeTheDefaults)
        {
            // The order the API has: the host's own sources exist before Program adds the defaults.
            configuration.AddInMemoryCollection(fromOutside);
            LogLevelDefaults.AddTo(configuration);
        }
        else
        {
            LogLevelDefaults.AddTo(configuration);
            configuration.AddInMemoryCollection(fromOutside);
        }

        using var loggers = LoggersFor(configuration);

        Assert.True(loggers.CreateLogger(FrameworkRequestLines).IsEnabled(LogLevel.Information));
        Assert.True(loggers.CreateLogger(OurOwnLines).IsEnabled(LogLevel.Debug));
    }

    private static ILoggerFactory LoggersFor(IConfiguration configuration) =>
        LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddProvider(new EverythingProvider());
        });

    /// <summary>A provider that would write any line, so that only the configured levels decide.</summary>
    private sealed class EverythingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new EverythingLogger();
        public void Dispose() { }

        private sealed class EverythingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }
}
