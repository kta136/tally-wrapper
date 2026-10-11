namespace ShowroomBilling.Contracts.Device;

/// <summary>
/// Shared between API and Desktop when both run on one machine as the same
/// Windows user (a development run): both read the same token file, which
/// gives them a local secret that gates write routes. A server install
/// trusts its LAN instead.
/// </summary>
public static class DeviceTokenConstants
{
    public const string HeaderName = "X-Device-Token";

    /// <summary>
    /// <c>%LOCALAPPDATA%\ShowroomBilling\device_token.txt</c>. Keeping this
    /// under LocalApplicationData (not Roaming) keeps the token machine-local;
    /// roaming would leak it onto other machines a user signs into.
    /// </summary>
    public static string ResolveTokenFilePath() => Path.Combine(AppPaths.LocalDirectory, "device_token.txt");
}
