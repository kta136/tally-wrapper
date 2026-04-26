using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Logging;

namespace ShowroomBilling.Tests;

public sealed class RollingFileLoggerTests
{
    [Fact]
    public void Log_WritesLineToFileWithCategoryAndMessage()
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
            var text = File.ReadAllText(file);
            Assert.Contains("[INF]", text);
            Assert.Contains("ShowroomBilling.Demo", text);
            Assert.Contains("hello world", text);
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
            }

            var file = Directory.EnumerateFiles(dir, "scoped-*.log").Single();
            var text = File.ReadAllText(file);
            Assert.Contains("CorrelationId=abc123", text);
            Assert.Contains("inside scope", text);
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
            Assert.Contains("should appear", text);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Retention_DeletesFilesOlderThanCutoff()
    {
        var dir = NewTempDir();
        try
        {
            var keep = Path.Combine(dir, "retain-20260421.log");
            var drop = Path.Combine(dir, "retain-20250101.log");
            File.WriteAllText(keep, "recent");
            File.WriteAllText(drop, "ancient");
            File.SetLastWriteTime(drop, DateTime.Now.AddDays(-90));

            using var provider = NewProvider(dir, "retain");

            Assert.True(File.Exists(keep));
            Assert.False(File.Exists(drop));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static RollingFileLoggerProvider NewProvider(string dir, string prefix, string minLevel = "Information") =>
        new(new RollingFileLoggerOptions
        {
            Enabled = true,
            Directory = dir,
            FilePrefix = prefix,
            RetentionDays = 30,
            FileSizeLimitMB = 20,
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
