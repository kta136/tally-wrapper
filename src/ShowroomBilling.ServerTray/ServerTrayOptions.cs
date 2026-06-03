namespace ShowroomBilling.ServerTray;

public sealed class ServerTrayOptions
{
    public string ServiceName { get; init; } =
        Environment.GetEnvironmentVariable("SHOWROOM_SERVERTRAY_SERVICE_NAME")
        ?? "ShowroomBilling.Api";

    public string ApiBaseUrl { get; init; } =
        Environment.GetEnvironmentVariable("SHOWROOM_SERVERTRAY_API_BASE_URL")
        ?? "http://127.0.0.1:5107";

    public string ConfigRoot { get; init; } =
        Environment.GetEnvironmentVariable("SHOWROOM_BILLING_APPDATA")
        ?? @"C:\ProgramData\ShowroomBilling";

    public string LogsPath => Path.Combine(ConfigRoot, "logs");

    public string BinPath => Path.Combine(ConfigRoot, "bin");

    public string ApiExecutablePath => Path.Combine(BinPath, "ShowroomBilling.Api.exe");

    public string InstallLogPath => Path.Combine(LogsPath, "server-install.log");

    public string TrayExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;
}
