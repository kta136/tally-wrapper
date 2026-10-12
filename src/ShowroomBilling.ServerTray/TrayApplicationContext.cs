using ShowroomBilling.ServerTray.Ui;

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
            Text = ServerTrayOptions.ProductName,
            Visible = true,
            ContextMenuStrip = BuildMenu(_actions, ShowStatus, StopServiceAndExitTray)
        };
        _notifyIcon.DoubleClick += (_, _) => ShowStatus();

        _timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _timer.Tick += (_, _) => RefreshTrayText();
        _timer.Start();
        RefreshTrayText();
    }

    /// <summary>The tray icon's menu: the dashboard first, what stops the server last and in red.</summary>
    internal static ContextMenuStrip BuildMenu(ServerTrayActions actions, Action openDashboard, Action stopServerAndExit)
    {
        var menu = new ContextMenuStrip();
        // What a double-click on the icon does, so it is the one in bold.
        var open = menu.Items.Add("Open dashboard", null, (_, _) => openDashboard());
        open.Font = Theme.Font(TextRole.BodyStrong);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start service", null, (_, _) => actions.StartService());
        menu.Items.Add("Stop service", null, (_, _) => actions.StopService());
        menu.Items.Add("Restart service", null, (_, _) => actions.RestartService());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy address for counters", null, (_, _) => actions.CopyServerUrl());
        menu.Items.Add("Open logs", null, (_, _) => actions.OpenLogs());
        menu.Items.Add("Open health page", null, (_, _) => actions.OpenLocalHealth());
        menu.Items.Add("Repair installation…", null, (_, _) => actions.InstallOrRepairServer());
        menu.Items.Add(new ToolStripSeparator());
        var exit = menu.Items.Add("Stop server and exit", null, (_, _) => stopServerAndExit());
        exit.ForeColor = Theme.ErrText;

        TrayMenuRenderer.Apply(menu);
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
        _notifyIcon.Text = $"{ServerTrayOptions.ProductName}: {ServerTrayActions.Describe(_actions.GetServiceStatus())}";
    }

    private void StopServiceAndExitTray()
    {
        var owner = _statusForm is { IsDisposed: false, Visible: true } ? _statusForm : null;
        var confirmed = TrayDialog.Confirm(
            owner,
            title: "Stop the server and close?",
            message: "This stops the Windows service and closes Tally Wrapper Server on this PC.",
            note: "Counters cannot bill until the service is started again.",
            confirmText: "Stop server");
        if (!confirmed)
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
