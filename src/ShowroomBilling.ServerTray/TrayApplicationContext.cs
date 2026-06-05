namespace ShowroomBilling.ServerTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ServerTrayOptions _options;
    private readonly ServerTrayActions _actions;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusForm? _statusForm;

    public TrayApplicationContext(ServerTrayOptions options)
    {
        _options = options;
        _actions = new ServerTrayActions(options);
        _actions.StatusChanged += (_, _) => RefreshTrayText();
        _notifyIcon = new NotifyIcon
        {
            Icon = AppIconProvider.CreateIcon(),
            Text = "Tally Wrapper Server",
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
        menu.Items.Add("Open Server Dashboard", null, (_, _) => ShowStatus());
        menu.Items.Add("Install / Repair Server", null, (_, _) => _actions.InstallOrRepairServer());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start API Service", null, (_, _) => _actions.StartService());
        menu.Items.Add("Stop API Service", null, (_, _) => _actions.StopService());
        menu.Items.Add("Restart API Service", null, (_, _) => _actions.RestartService());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Logs", null, (_, _) => _actions.OpenLogs());
        menu.Items.Add("Open Local Health", null, (_, _) => _actions.OpenLocalHealth());
        menu.Items.Add("Copy Server URL", null, (_, _) => _actions.CopyServerUrl());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Tray", null, (_, _) => StopServiceAndExitTray());
        return menu;
    }

    private void ShowStatus()
    {
        if (_statusForm is { IsDisposed: false })
        {
            _statusForm.Activate();
            return;
        }

        _statusForm = new StatusForm(_options, _actions, StopServiceAndExitTray);
        _statusForm.Show();
    }

    private void RefreshTrayText()
    {
        _notifyIcon.Text = _actions.GetServiceStatusText().Replace("Service:", "Tally Wrapper Server:");
    }

    private void StopServiceAndExitTray()
    {
        var result = MessageBox.Show(
            "Stop the API Windows Service and close the tray?\n\nBilling workstations will lose server access until the service is started again.",
            "Tally Wrapper Server",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
        {
            return;
        }

        if (_actions.StopService())
        {
            ExitThread();
        }
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
