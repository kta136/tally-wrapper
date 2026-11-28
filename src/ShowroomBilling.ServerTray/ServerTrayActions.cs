using System.Diagnostics;
using System.ServiceProcess;
using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.ServerTray;

public sealed class ServerTrayActions(ServerTrayOptions options)
{
    public event EventHandler? StatusChanged;

    public InstallOutcome InstallOrRepairServer(IWin32Window? owner = null)
    {
        var outcome = ServerInstaller.EnsureInstalledInteractive(options, forceRepair: true);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        var report = InstallReport(
            outcome, GetServiceStatus(), options.InstallLogPath, ServerInstaller.InstallLogTail(options));
        report.Show(owner);
        return outcome;
    }

    /// <summary>
    /// The one message about an install or repair. "Installed and running" is
    /// said only when the service is running: setup finishing does not prove
    /// that, because a service that will not start is not a setup failure.
    /// </summary>
    internal static TrayMessage InstallReport(
        InstallOutcome outcome,
        ServiceControllerStatus? service,
        string installLogPath,
        string installLogTail) => outcome switch
    {
        InstallOutcome.Done when service == ServiceControllerStatus.Running =>
            new("The server is installed and running.", null, Tone.Ok),
        InstallOutcome.Done =>
            new($"The server is installed, but its Windows service is {Describe(service)}.", $"Start it from the dashboard. If it will not start, the install log has the reason: {installLogPath}",
                Tone.Warn),
        InstallOutcome.NotStarted =>
            new("Setup did not finish.", "It was cancelled, or Windows did not give permission. Nothing was changed.",
                Tone.Warn),
        _ =>
            new("Setup failed.", $"The end of the install log, {installLogPath}:\n\n{installLogTail}",
                Tone.Err),
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

        problem.Show(owner);
        return false;
    }

    /// <summary>
    /// Starts, stops or restarts the service. Null when it worked, otherwise
    /// what to tell the operator. The tray runs with the signed-in user's
    /// ordinary rights, and Windows lets only an administrator do this, so a
    /// refusal is answered by doing it in an elevated copy of the tray, behind
    /// the same Windows permission prompt as Install.
    /// </summary>
    private TrayMessage? TryControlService(ServiceCommand action)
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
                null => new($"The service did not {verb}.", "Windows asked for permission and it was not given.", Tone.Err),
                _ => new($"The service did not {verb}.", $"The install log has the reason: {options.InstallLogPath}", Tone.Err),
            };
        }
        catch (Exception ex)
        {
            return new($"The service did not {verb}.", ex.Message, Tone.Err);
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
