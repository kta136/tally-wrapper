using System.Diagnostics;
using System.ServiceProcess;

namespace ShowroomBilling.ServerTray;

public sealed class ServerTrayActions(ServerTrayOptions options)
{
    public event EventHandler? StatusChanged;

    public InstallOutcome InstallOrRepairServer(IWin32Window? owner = null)
    {
        var outcome = ServerInstaller.EnsureInstalledInteractive(options, forceRepair: true);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        var (message, icon) = InstallReport(
            outcome, GetServiceStatus(), options.InstallLogPath, ServerInstaller.InstallLogTail(options));
        CopyableMessageDialog.ShowMessage(owner, message, icon);
        return outcome;
    }

    /// <summary>
    /// The one message about an install or repair. "Installed and running" is
    /// said only when the service is running: setup finishing does not prove
    /// that, because a service that will not start is not a setup failure.
    /// </summary>
    internal static (string Message, MessageBoxIcon Icon) InstallReport(
        InstallOutcome outcome,
        ServiceControllerStatus? service,
        string installLogPath,
        string installLogTail) => outcome switch
    {
        InstallOutcome.Done when service == ServiceControllerStatus.Running =>
            ("The server is installed and running.", MessageBoxIcon.Information),
        InstallOutcome.Done =>
            ($"The server is installed, but its Windows service is {Describe(service)}.\n\nStart it from the dashboard. If it will not start, the install log has the reason: {installLogPath}",
                MessageBoxIcon.Warning),
        InstallOutcome.NotStarted =>
            ("Setup did not finish.\n\nIt was cancelled, or Windows did not give permission. Nothing was changed.",
                MessageBoxIcon.Warning),
        _ =>
            ($"Setup failed.\n\nThe end of the install log, {installLogPath}:\n\n{installLogTail}",
                MessageBoxIcon.Error),
    };

    public Task<bool> StartServiceAsync(IWin32Window? owner = null) => ControlServiceAsync(ServiceCommand.Start, owner);

    public Task<bool> StopServiceAsync(IWin32Window? owner = null) => ControlServiceAsync(ServiceCommand.Stop, owner);

    public Task<bool> RestartServiceAsync(IWin32Window? owner = null) => ControlServiceAsync(ServiceCommand.Restart, owner);

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

    /// <summary>
    /// A service takes up to 20 seconds to start or stop, so the waiting is
    /// done off the UI thread; the window used to freeze for all of it. The
    /// message, if there is one, is shown back on the UI thread.
    /// </summary>
    private async Task<bool> ControlServiceAsync(ServiceCommand action, IWin32Window? owner)
    {
        var problem = await Task.Run(() => TryControlService(action));
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
