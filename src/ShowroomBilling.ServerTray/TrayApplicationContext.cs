using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.ServerTray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ServerTrayOptions _options;
    private readonly ServerTrayActions _actions;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusForm? _statusForm;
    private readonly SynchronizationContext _ui;

    /// <param name="showDashboard">Open the dashboard at once: this copy took over from one that was running, and the operator who started it expects to see something.</param>
    public TrayApplicationContext(ServerTrayOptions options, bool showDashboard = false)
    {
        _options = options;
        _ui = SynchronizationContext.Current as WindowsFormsSynchronizationContext ?? new WindowsFormsSynchronizationContext();
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
        if (showDashboard)
        {
            _ui.Post(_ => ShowStatus(), null);
        }
    }

    /// <summary>Closes the tray from any thread, leaving the service as it is: another copy is taking over.</summary>
    public void Close() => _ui.Post(_ => ExitThread(), null);

    /// <summary>The tray icon's menu: the dashboard first, what stops the server last and in red.</summary>
    internal static ContextMenuStrip BuildMenu(ServerTrayActions actions, Action openDashboard, Action stopServerAndExit)
    {
        var menu = new ContextMenuStrip();
        // What a double-click on the icon does, so it is the one in bold.
        var open = menu.Items.Add("Open dashboard", null, (_, _) => openDashboard());
        open.Font = Theme.Font(TextRole.BodyStrong);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start service", null, async (_, _) => await actions.StartServiceAsync());
        menu.Items.Add("Stop service", null, async (_, _) => await actions.StopServiceAsync());
        menu.Items.Add("Restart service", null, async (_, _) => await actions.RestartServiceAsync());
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

    private async void StopServiceAndExitTray()
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

        try
        {
            if (await _actions.StopServiceAsync(owner))
            {
                ExitThread();
            }
        }
        catch (Exception ex)
        {
            // Nothing awaits this handler; an error let out of it would end the tray.
            TrayDialog.ShowMessage(owner, Tone.Err, "The service did not stop.", ex.Message);
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
