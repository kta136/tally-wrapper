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

    public bool StartService(IWin32Window? owner = null) => ControlService(ServiceCommand.Start, owner);

    public bool StopService(IWin32Window? owner = null) => ControlService(ServiceCommand.Stop, owner);

    public bool RestartService(IWin32Window? owner = null) => ControlService(ServiceCommand.Restart, owner);

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

    private bool ControlService(ServiceCommand action, IWin32Window? owner)
    {
        var problem = TryControlService(action);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (problem is null)
        {
            return true;
        }

        CopyableMessageDialog.ShowMessage(owner, problem, MessageBoxIcon.Error);
        return false;
    }

    /// <summary>
    /// Starts, stops or restarts the service. Null when it worked, otherwise
    /// what to tell the operator. The tray runs with the signed-in user's
    /// ordinary rights, and Windows lets only an administrator do this, so a
    /// refusal is answered by doing it in an elevated copy of the tray, behind
    /// the same Windows permission prompt as Install.
    /// </summary>
    private string? TryControlService(ServiceCommand action)
    {
        var verb = ServiceCommands.Verb(action);
        try
        {
            ServiceCommands.Apply(options.ServiceName, action);
            return null;
        }
        catch (Exception ex) when (ServiceCommands.IsAccessDenied(ex))
        {
            return ServerInstaller.ControlServiceElevated(action) switch
            {
                0 => null,
                null => $"The service did not {verb}.\n\nWindows asked for permission and it was not given.",
                _ => $"The service did not {verb}.\n\nThe install log has the reason: {options.InstallLogPath}",
            };
        }
        catch (Exception ex)
        {
            return $"The service did not {verb}.\n\n{ex.Message}";
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
