namespace ShowroomBilling.Contracts.Device;

/// <summary>
/// Shared between API and Desktop: both processes read the same token file
/// (both run as the same Windows user under ChildProcessSupervisor) to
/// establish a local IPC secret that gates write routes.
/// </summary>
public static class DeviceTokenConstants
{
    public const string HeaderName = "X-Device-Token";

    /// <summary>
    /// <c>%LOCALAPPDATA%\ShowroomBilling\device_token.txt</c>. Keeping this
    /// under LocalApplicationData (not Roaming) keeps the token machine-local;
    /// roaming would leak it onto other machines a user signs into.
    /// </summary>
    public static string ResolveTokenFilePath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "ShowroomBilling", "device_token.txt");
    }
}
