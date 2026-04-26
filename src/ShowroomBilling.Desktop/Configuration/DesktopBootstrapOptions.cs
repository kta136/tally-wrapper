namespace ShowroomBilling.Desktop.Configuration;

public sealed class DesktopBootstrapOptions
{
    public const string SectionName = "DesktopBootstrap";

    public string ApiBaseUrl { get; init; } = "http://localhost:5107";

    public string DeviceId { get; init; } = "desktop-dev-01";

    public string CounterName { get; init; } = "Counter 1";

    public string StartupMode { get; init; } = "OnlineOnly";
}
