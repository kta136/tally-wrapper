namespace ShowroomBilling.Desktop.Configuration;

public sealed class DesktopBootstrapOptions
{
    public const string SectionName = "DesktopBootstrap";

    /// <summary>
    /// Address of the Tally Wrapper server API this workstation works against.
    /// The Desktop never starts an API of its own.
    /// </summary>
    public string ApiBaseUrl { get; init; } = "http://localhost:5107";

    public string DeviceId { get; init; } = "desktop-dev-01";

    public string CounterName { get; init; } = "Counter 1";
}
