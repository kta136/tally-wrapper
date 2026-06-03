using System.Diagnostics;
using System.ServiceProcess;

namespace ShowroomBilling.ServerTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ServerTrayOptions _options;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusForm? _statusForm;

    public TrayApplicationContext(ServerTrayOptions options)
    {
        _options = options;
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Showroom Billing Server",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatus();

        _timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _timer.Tick += (_, _) => RefreshTrayText();
        _timer.Start();
        RefreshTrayText();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Status", null, (_, _) => ShowStatus());
        menu.Items.Add("Install / Repair Server", null, (_, _) => InstallOrRepairServer());
        menu.Items.Add("Start API Service", null, (_, _) => ControlService(ServiceControllerStatus.Running));
        menu.Items.Add("Stop API Service", null, (_, _) => ControlService(ServiceControllerStatus.Stopped));
        menu.Items.Add("Restart API Service", null, (_, _) => RestartService());
        menu.Items.Add("Open Logs", null, (_, _) => OpenPath(_options.LogsPath));
        menu.Items.Add("Open Local Health", null, (_, _) => OpenUrl($"{_options.ApiBaseUrl.TrimEnd('/')}/api/health/live"));
        menu.Items.Add("Copy Server URL", null, (_, _) => Clipboard.SetText(ServerUrlHelper.GetWorkstationApiBaseUrl(_options.ApiBaseUrl)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Tray", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowStatus()
    {
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.Activate();
            return;
        }

        _statusForm = new StatusForm(_options);
        _statusForm.Show();
    }

    private void RefreshTrayText()
    {
        try
        {
            using var service = new ServiceController(_options.ServiceName);
            _notifyIcon.Text = $"Showroom Billing Server: {service.Status}";
        }
        catch
        {
            _notifyIcon.Text = "Showroom Billing Server: service not installed";
        }
    }

    private void InstallOrRepairServer()
    {
        var installed = ServerInstaller.EnsureInstalledInteractive(_options, forceRepair: true);
        RefreshTrayText();
        MessageBox.Show(
            installed
                ? "Server setup is installed, repaired, and started."
                : "Server setup was cancelled or did not complete.",
            "Showroom Billing Server",
            MessageBoxButtons.OK,
            installed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void ControlService(ServiceControllerStatus target)
    {
        try
        {
            using var service = new ServiceController(_options.ServiceName);
            if (target == ServiceControllerStatus.Running && service.Status != ServiceControllerStatus.Running)
            {
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
            else if (target == ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            RefreshTrayText();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Service control failed: {ex.Message}\n\nRun the tray as Administrator or grant service-control rights to this user.",
                "Showroom Billing Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void RestartService()
    {
        ControlService(ServiceControllerStatus.Stopped);
        ControlService(ServiceControllerStatus.Running);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _statusForm?.Dispose();
        }

        base.Dispose(disposing);
    }
}
