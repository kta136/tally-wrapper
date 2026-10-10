using System.Diagnostics;
using System.IO;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal static class SettingsFolderLauncher
{
    public static string AppDataFolderPath => AppPaths.DataDirectory;

    public static string LogFolderPath => AppPaths.LogsDirectory;

    public static void OpenLogFolder() => OpenCreatingFolder(LogFolderPath);

    public static void OpenAppDataFolder() => OpenCreatingFolder(AppDataFolderPath);

    public static void OpenInstallFolder() => OpenFolder(AppContext.BaseDirectory);

    private static void OpenCreatingFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenFolder(path);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best effort: the visible path still lets the operator open it manually.
        }
    }
}
