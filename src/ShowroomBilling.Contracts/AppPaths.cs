namespace ShowroomBilling.Contracts;

/// <summary>
/// Where the Desktop, the API and the server tray keep their files on a
/// machine. Every path under the app's folders is built from here, so the
/// folder can be moved (the server install does) and tests can point the whole
/// process at a scratch folder instead of the developer's real settings.
/// </summary>
public static class AppPaths
{
    public const string FolderName = "ShowroomBilling";

    /// <summary>
    /// When set, replaces <c>%APPDATA%\ShowroomBilling</c> as
    /// <see cref="DataDirectory"/>. The server install sets it to
    /// <c>C:\ProgramData\ShowroomBilling</c> for the service.
    /// </summary>
    public const string DataDirectoryEnvironmentVariable = "SHOWROOM_BILLING_APPDATA";

    /// <summary>
    /// When set, replaces <c>%LOCALAPPDATA%\ShowroomBilling</c> as
    /// <see cref="LocalDirectory"/>. Only the test projects set it.
    /// </summary>
    public const string LocalDirectoryEnvironmentVariable = "SHOWROOM_BILLING_LOCALAPPDATA";

    /// <summary>Settings, local overrides, preferences and logs.</summary>
    public static string DataDirectory => Resolve(
        DataDirectoryEnvironmentVariable, Environment.SpecialFolder.ApplicationData);

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Files that must stay on this machine and not follow a roaming profile:
    /// the device token.
    /// </summary>
    public static string LocalDirectory => Resolve(
        LocalDirectoryEnvironmentVariable, Environment.SpecialFolder.LocalApplicationData);

    private static string Resolve(string environmentVariable, Environment.SpecialFolder folder)
        => Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(folder), FolderName);
}
