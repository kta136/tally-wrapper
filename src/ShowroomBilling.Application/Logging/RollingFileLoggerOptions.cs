using ShowroomBilling.Contracts;
namespace ShowroomBilling.Application.Logging;

public sealed class RollingFileLoggerOptions
{
    public const string SectionName = "Logging:File";

    public bool Enabled { get; set; } = true;

    public string Directory { get; set; } = DefaultDirectory();

    /// <summary>Placeholder prefix; <c>AddRollingFile</c> replaces it with the host's own.</summary>
    public const string DefaultFilePrefix = "host";

    public string FilePrefix { get; set; } = DefaultFilePrefix;

    public int RetentionDays { get; set; } = 30;

    public int FileSizeLimitMB { get; set; } = 20;

    public string MinLevel { get; set; } = "Information";

    public static string DefaultDirectory() => AppPaths.LogsDirectory;
}
