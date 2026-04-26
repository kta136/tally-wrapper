namespace ShowroomBilling.Application.Logging;

public sealed class RollingFileLoggerOptions
{
    public const string SectionName = "Logging:File";

    public bool Enabled { get; set; } = true;

    public string Directory { get; set; } = DefaultDirectory();

    public string FilePrefix { get; set; } = "host";

    public int RetentionDays { get; set; } = 30;

    public int FileSizeLimitMB { get; set; } = 20;

    public string MinLevel { get; set; } = "Information";

    public static string DefaultDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ShowroomBilling", "logs");
    }
}
