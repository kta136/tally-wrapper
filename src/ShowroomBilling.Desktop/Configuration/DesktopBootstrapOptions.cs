namespace ShowroomBilling.Desktop.Configuration;

public sealed class DesktopBootstrapOptions
{
    public const string SectionName = "DesktopBootstrap";

    public string ApiBaseUrl { get; init; } = "http://localhost:5107";

    public string ServerApiBaseUrl { get; init; } = "http://localhost:5107";

    public string ConnectionMode { get; init; } = DesktopConnectionModes.LocalEmbedded;

    public string DeviceId { get; init; } = "desktop-dev-01";

    public string CounterName { get; init; } = "Counter 1";

    public string StartupMode { get; init; } = "OnlineOnly";

    public bool IsServerMode =>
        string.Equals(ConnectionMode, DesktopConnectionModes.Server, StringComparison.OrdinalIgnoreCase);

    public string EffectiveApiBaseUrl =>
        IsServerMode && !string.IsNullOrWhiteSpace(ServerApiBaseUrl)
            ? ServerApiBaseUrl
            : ApiBaseUrl;
}

public static class DesktopConnectionModes
{
    public const string LocalEmbedded = "LocalEmbedded";
    public const string Server = "Server";
}
