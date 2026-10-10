using System.Diagnostics;

namespace ShowroomBilling.Desktop.Services;

internal static class AppRestart
{
    /// <summary>Starts a new copy of this executable, then shuts this one down.</summary>
    public static void Restart()
    {
        var executable = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            });
        }

        System.Windows.Application.Current.Shutdown();
    }
}
