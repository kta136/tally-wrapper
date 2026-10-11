using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Logging;

namespace ShowroomBilling.Tests;

public sealed class RollingFileLoggingTests
{
    [Fact]
    public void Log_WritesOneLineWithTimestampLevelCategoryAndMessage()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = NewProvider(dir, "test"))
            {
                var logger = provider.CreateLogger("ShowroomBilling.Demo");
                logger.LogInformation("hello {Who}", "world");
            }

            var file = Directory.EnumerateFiles(dir, "test-*.log").Single();
            Assert.Equal($"test-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
            Assert.Matches(
                new Regex(@"\A\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}[+-]\d\d:\d\d \[INF\] ShowroomBilling\.Demo: hello world\r?\n\z"),
                File.ReadAllText(file));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Log_WritesTheEventIdAndTheExceptionBlock()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = NewProvider(dir, "error"))
            {
                var logger = provider.CreateLogger("ShowroomBilling.Demo");
                logger.LogError(new EventId(42, "SaveFailed"), new InvalidOperationException("disk full"), "failed to {Step}", "save");
            }

            var lines = File.ReadAllLines(Directory.EnumerateFiles(dir, "error-*.log").Single());
            Assert.EndsWith("[ERR] ShowroomBilling.Demo (42:SaveFailed): failed to save", lines[0]);
            Assert.Equal("System.InvalidOperationException: disk full", lines[1]);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Log_WhenTheDayFileIsLocked_WritesToTheNextFileInSequence()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, $"locked-{DateTime.Now:yyyyMMdd}.log");
            using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            using (var provider = NewProvider(dir, "locked"))
            {
                // Antivirus, or a second copy of the same host, is holding the file.
                provider.CreateLogger("ShowroomBilling.Lock").LogInformation("written while locked");
            }

            var next = Path.Combine(dir, $"locked-{DateTime.Now:yyyyMMdd}_001.log");
            Assert.Contains("written while locked", File.ReadAllText(next));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Log_IncludesScopePropertiesInOutput()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = NewProvider(dir, "scoped"))
            {
                var logger = provider.CreateLogger("ShowroomBilling.Scope");
                using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = "abc123" }))
                {
                    logger.LogInformation("inside scope");
                }
                logger.LogInformation("outside scope");
            }

            var lines = File.ReadAllLines(Directory.EnumerateFiles(dir, "scoped-*.log").Single());
            Assert.EndsWith("[INF] [CorrelationId=abc123] ShowroomBilling.Scope: inside scope", lines[0]);
            Assert.EndsWith("[INF] ShowroomBilling.Scope: outside scope", lines[1]);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Log_BelowMinLevel_IsSuppressed()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = NewProvider(dir, "level", minLevel: "Warning"))
            {
                var logger = provider.CreateLogger("cat");
                logger.LogInformation("should be skipped");
                logger.LogWarning("should appear");
            }

            var file = Directory.EnumerateFiles(dir, "level-*.log").Single();
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("should be skipped", text);
            Assert.Contains("[WRN] cat: should appear", text);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Disabled_WritesNothing()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = NewProvider(dir, "off", enabled: false))
            {
                provider.CreateLogger("cat").LogError("not written");
            }

            Assert.Empty(Directory.EnumerateFiles(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Retention_DeletesFilesDatedBeforeTheCutoff()
    {
        var dir = NewTempDir();
        try
        {
            // Age is read from the date in the file name.
            var keep = Path.Combine(dir, $"retain-{DateTime.Now.AddDays(-5):yyyyMMdd}.log");
            var drop = Path.Combine(dir, $"retain-{DateTime.Now.AddDays(-90):yyyyMMdd}.log");
            var otherHost = Path.Combine(dir, $"other-{DateTime.Now.AddDays(-90):yyyyMMdd}.log");
            File.WriteAllText(keep, "recent");
            File.WriteAllText(drop, "ancient");
            File.WriteAllText(otherHost, "another prefix");

            using (var provider = NewProvider(dir, "retain"))
            {
                // Old files are cleared when the day's file is opened.
                provider.CreateLogger("cat").LogInformation("first line of the day");
            }

            Assert.True(File.Exists(keep));
            Assert.False(File.Exists(drop));
            Assert.True(File.Exists(otherHost));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Log_PastTheSizeLimit_ContinuesInANumberedFile()
    {
        var dir = NewTempDir();
        try
        {
            using (var provider = NewProvider(dir, "size", fileSizeLimitMB: 1))
            {
                var logger = provider.CreateLogger("cat");
                var block = new string('x', 200 * 1024);
                for (var i = 0; i < 7; i++)
                {
                    logger.LogInformation("{Index} {Block}", i, block);
                }
            }

            var names = Directory.EnumerateFiles(dir, "size-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            Assert.Equal([$"size-{date}.log", $"size-{date}_001.log"], names);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task Log_ReachesTheFileDuringAQuietSpell_WithoutAnotherLineOrDispose()
    {
        var dir = NewTempDir();
        try
        {
            using var provider = NewProvider(dir, "quiet");
            provider.CreateLogger("ShowroomBilling.Quiet").LogInformation("last line before the crash");

            // Nothing else is logged and the provider stays open, as when the
            // process is about to be killed. The line must still get flushed.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var text = string.Empty;
            while (DateTime.UtcNow < deadline && !text.Contains("last line before the crash", StringComparison.Ordinal))
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
                text = ReadShared(dir, "quiet-*.log");
            }

            Assert.Contains("last line before the crash", text);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string ReadShared(string dir, string pattern)
    {
        var text = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(dir, pattern))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }
        return text.ToString();
    }

    private static ILoggerProvider NewProvider(
        string dir,
        string prefix,
        string minLevel = "Information",
        bool enabled = true,
        int fileSizeLimitMB = 20) =>
        LoggingExtensions.CreateRollingFileProvider(new RollingFileLoggerOptions
        {
            Enabled = enabled,
            Directory = dir,
            FilePrefix = prefix,
            RetentionDays = 30,
            FileSizeLimitMB = fileSizeLimitMB,
            MinLevel = minLevel
        });

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "sbv2-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
