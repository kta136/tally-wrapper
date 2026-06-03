namespace ShowroomBilling.Api.Options;

public sealed class DeviceAuthOptions
{
    public const string SectionName = "DeviceAuth";

    public string Mode { get; set; } = DeviceAuthModes.LocalFile;

    public string[] TrustedNetworks { get; set; } = [];

    public bool IsTrustedLan =>
        string.Equals(Mode, DeviceAuthModes.TrustedLan, StringComparison.OrdinalIgnoreCase);
}

public static class DeviceAuthModes
{
    public const string LocalFile = "LocalFile";
    public const string TrustedLan = "TrustedLan";
}
