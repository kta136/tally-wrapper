using System.Diagnostics;
using System.ServiceProcess;

namespace ShowroomBilling.ServerTray;

public sealed class ServerTrayActions(ServerTrayOptions options)
{
    public event EventHandler? StatusChanged;

    public bool InstallOrRepairServer(IWin32Window? owner = null)
    {
        var installed = ServerInstaller.EnsureInstalledInteractive(options, forceRepair: true);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        CopyableMessageDialog.ShowMessage(
            owner,
            installed
                ? "The server is installed and running."
                : "Setup did not finish.\n\nIt was cancelled, or Windows did not allow it. The install log has the details.",
            installed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return installed;
    }

    public bool StartService(IWin32Window? owner = null) =>
        ControlService(ServiceControllerStatus.Running, "start", owner);

    public bool StopService(IWin32Window? owner = null) =>
        ControlService(ServiceControllerStatus.Stopped, "stop", owner);

    public bool RestartService(IWin32Window? owner = null)
    {
        if (!StopService(owner))
        {
            return false;
        }

        return StartService(owner);
    }

    /// <summary>The API service's status, or null when it is not installed.</summary>
    public ServiceControllerStatus? GetServiceStatus() => ServerInstaller.TryGetServiceStatus(options.ServiceName);

    /// <summary>The status as the tray icon's tooltip says it: "running", "stopped", "not installed".</summary>
    public static string Describe(ServiceControllerStatus? status) => status switch
    {
        null => "not installed",
        ServiceControllerStatus.Running => "running",
        ServiceControllerStatus.Stopped => "stopped",
        ServiceControllerStatus.StartPending => "starting",
        ServiceControllerStatus.StopPending => "stopping",
        _ => "paused",
    };

    public string GetWorkstationUrl() => ServerUrlHelper.GetWorkstationApiBaseUrl(options.ApiBaseUrl);

    public void CopyServerUrl() => Clipboard.SetText(GetWorkstationUrl());

    public void OpenLogs() => OpenPath(options.LogsPath);

    public void OpenLocalHealth() => OpenUrl($"{options.ApiBaseUrl.TrimEnd('/')}/api/health/live");

    public void OpenConfigRoot() => OpenPath(options.ConfigRoot);

    public void OpenInstallLog()
    {
        Directory.CreateDirectory(options.LogsPath);
        if (!File.Exists(options.InstallLogPath))
        {
            File.WriteAllText(options.InstallLogPath, string.Empty);
        }

        Process.Start(new ProcessStartInfo { FileName = options.InstallLogPath, UseShellExecute = true });
    }

    private bool ControlService(ServiceControllerStatus target, string verb, IWin32Window? owner)
    {
        try
        {
            using var service = new ServiceController(options.ServiceName);
            service.Refresh();

            if (target == ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.Running)
            {
                if (service.Status == ServiceControllerStatus.StopPending)
                {
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    service.Refresh();
                }

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            else if (target == ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            CopyableMessageDialog.ShowMessage(
                owner,
                $"The service did not {verb}.\n\n{ex.Message}\n\nWindows lets an administrator start and stop services. Run Tally Wrapper Server as administrator, or give this user that right.",
                MessageBoxIcon.Error);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
