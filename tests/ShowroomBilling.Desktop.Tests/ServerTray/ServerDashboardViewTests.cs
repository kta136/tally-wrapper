using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Windows.Forms;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Tests.Views;
using ShowroomBilling.ServerTray;
using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.Desktop.Tests.ServerTray;

/// <summary>
/// The server dashboard and its dialogs, laid out without being shown: no
/// button or chip may lose part of its words, and nothing may hang out of its
/// box. With <c>TALLYWRAPPER_VIEW_SNAPSHOTS</c> set, each is also saved as a
/// PNG named <c>Server-*.png</c>, which is how to look at them after a change.
/// <para>
/// The pictures are drawn at 100 % scaling. To see them at the scaling of
/// this PC's screen instead, also set <c>TALLYWRAPPER_SNAPSHOT_SCALED=1</c>
/// and run this class by itself: the setting applies to the whole test process
/// and has to be made before anything in it opens a window.
/// </para>
/// </summary>
public sealed class ServerDashboardViewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 12, 41, 7, TimeSpan.FromMinutes(330));

    [Fact]
    public void The_dashboard_fits_its_words_in_every_state()
    {
        OnStaThread(() =>
        {
            var options = new ServerTrayOptions
            {
                ServiceName = "TallyWrapper.NoSuchService.Test",
                ApiBaseUrl = "http://127.0.0.1:9",
                ConfigRoot = Path.Combine(Path.GetTempPath(), "tally-wrapper-dashboard-test"),
            };
            using var form = new StatusForm(options, new ServerTrayActions(options), () => { });
            Realize(form);

            foreach (var (name, state) in States())
            {
                form.Render(state);
                // Again: a button put into a window that is not on screen has no handle yet.
                Realize(form);

                AssertWordsFit(form, name);
                Save(form, $"Server-{name}");
            }
        });
    }

    [Fact]
    public void A_dialog_is_as_tall_as_what_it_holds()
    {
        OnStaThread(() =>
        {
            var prompt = TrayDialog.TextPrompt(
                "Which PCs may use this server?",
                "Counters whose address is inside this network range can save bills. Any other PC is refused.",
                "192.168.1.0/24",
                "For a network where every address starts with 192.168.1, that is 192.168.1.0/24.",
                "Install",
                ServerInstaller.LanCidrProblem);
            var refused = TrayDialog.TextPrompt(
                "Which PCs may use this server?",
                "Counters whose address is inside this network range can save bills. Any other PC is refused.",
                "the whole shop",
                "For a network where every address starts with 192.168.1, that is 192.168.1.0/24.",
                "Install",
                ServerInstaller.LanCidrProblem);

            var dialogs = new (string Name, TrayDialog Dialog)[]
            {
                ("Dialog-Saved", TrayDialog.Message(
                    Tone.Ok,
                    "Save succeeded.",
                    "Stored: Host=db.example.net;Port=5432;Database=tally_wrapper_prod;Username=billing_api;Password=***\n\n"
                    + "The server starts using it when the Windows service restarts.")),
                ("Dialog-Failed", TrayDialog.Message(
                    Tone.Err,
                    "Connection test failed: HTTP 400",
                    "Bad Request: Connection failed: 28P01: password authentication failed for user \"billing_api\"")),
                ("Dialog-Done", TrayDialog.Message(Tone.Ok, "The server is installed and running.", null)),
                ("Dialog-Stop", TrayDialog.Confirmation(
                    "Stop the server and close?",
                    "This stops the Windows service and closes Tally Wrapper Server on this PC.",
                    "Counters cannot bill until the service is started again.",
                    "Stop server")),
                ("Dialog-Range", prompt),
                ("Dialog-RangeRefused", refused),
            };

            foreach (var (name, dialog) in dialogs)
            {
                using (dialog)
                {
                    Realize(dialog);
                    // What loading does; a dialog that is never shown is never loaded.
                    dialog.FitHeight();
                    if (ReferenceEquals(dialog, refused))
                    {
                        dialog.Accept();
                        Assert.Equal(DialogResult.None, dialog.DialogResult);
                    }

                    dialog.PerformLayout();
                    AssertWordsFit(dialog, name);

                    // The content ends where the grey foot begins: no gap, nothing under it.
                    var content = dialog.Controls.OfType<TableLayoutPanel>().Single();
                    var foot = dialog.Controls.OfType<FootBand>().Single();
                    Assert.True(
                        content.Bottom == foot.Top,
                        $"{name}: the content ends at {content.Bottom} and the foot starts at {foot.Top} (client height {dialog.ClientSize.Height}).");
                    Save(dialog, $"Server-{name}");
                }
            }
        });
    }

    [Fact]
    public void The_tray_menu_ends_with_the_item_that_stops_the_server()
    {
        OnStaThread(() =>
        {
            var options = new ServerTrayOptions { ServiceName = "TallyWrapper.NoSuchService.Test" };
            using var menu = TrayApplicationContext.BuildMenu(new ServerTrayActions(options), () => { }, () => { });

            Assert.Equal("Open dashboard", menu.Items[0].Text);
            Assert.Equal("Stop server and exit", menu.Items[^1].Text);
            Assert.IsType<ToolStripSeparator>(menu.Items[^2]);

            Realize(menu);
            menu.Size = menu.GetPreferredSize(Size.Empty);
            Save(menu, "Server-TrayMenu");
        });
    }

    private static IEnumerable<(string Name, DashboardState State)> States()
    {
        var counters = new[]
        {
            Counter("Counter 1", "SHOWROOM-PC1", "ravi", "192.168.1.21", 9),
            Counter("Counter 2", "SHOWROOM-PC2", "meera", "192.168.1.22", 21),
            Counter("Back office", "OFFICE-LAPTOP", "kartikey", "192.168.1.40", 4),
        };

        yield return ("Checking", DashboardState.Checking);
        yield return ("Ready", DashboardState.From(
            ServiceControllerStatus.Running, null, Runtime(reachable: true), counters, null, At));
        yield return ("DatabaseDown", DashboardState.From(
            ServiceControllerStatus.Running, null, Runtime(reachable: false), counters[..2], null, At));
        yield return ("UpgradeNeeded", DashboardState.From(
            ServiceControllerStatus.Running, null, Runtime(reachable: true, pending: 6), [], null, At));
        yield return ("Stopped", DashboardState.From(
            ServiceControllerStatus.Stopped,
            "No connection could be made because the target machine actively refused it. (127.0.0.1:5107)",
            null,
            null,
            null,
            At));
        yield return ("NotInstalled", DashboardState.From(null, "Connection refused (127.0.0.1:5107)", null, null, null, At));
    }

    private static RuntimeHealthResponse Runtime(bool reachable, int pending = 0) => new(
        reachable && pending == 0 ? "Healthy" : "Unhealthy",
        ApiAvailable: true,
        DatabaseConfigured: true,
        DatabaseReachable: reachable,
        SettingsLoadedFromApi: true,
        Message: "from the server",
        DatabaseIdentity: reachable ? "PROD" : null,
        ExpectedDatabaseIdentity: "PROD",
        DatabaseIdentityMatches: reachable ? true : null,
        PendingMigrationCount: pending);

    private static ClientPresenceResponse Counter(string name, string pc, string user, string address, int secondsAgo) => new(
        Guid.NewGuid().ToString("N"),
        name,
        "1.0",
        pc,
        user,
        address,
        At.AddHours(-2),
        At.AddSeconds(-secondsAgo),
        At.AddMinutes(2));

    /// <summary>
    /// Creates the window handles of a form that is never shown, which is what
    /// lays it out and lets it be drawn. Showing it would take the keyboard
    /// from whoever is running the tests.
    /// </summary>
    private static void Realize(Control window)
    {
        var createControl = typeof(Control).GetMethod(
                "CreateControl", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(bool)])
            ?? throw new InvalidOperationException("Control.CreateControl(bool) is gone: find another way to lay out a hidden form.");
        createControl.Invoke(window, [true]);
        window.PerformLayout();
    }

    /// <summary>Whether the control is set to show, whatever the window it sits in is doing.</summary>
    private static bool IsSetVisible(Control control)
    {
        var desired = typeof(Control).GetProperty("DesiredVisibility", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Control.DesiredVisibility is gone: find another way to tell hidden buttons apart.");
        return (bool)desired.GetValue(control)!;
    }

    private static void AssertWordsFit(Control window, string name)
    {
        foreach (var control in Descendants(window))
        {
            var isOneLine = control is FlatButton or Chip || (control is TextLabel && control.Dock == DockStyle.None);
            if (isOneLine)
            {
                var needed = control.GetPreferredSize(Size.Empty).Width;
                Assert.True(
                    needed <= control.Width + 1,
                    $"{name}: '{control.Text}' needs {needed} px and has {control.Width}.");
            }

            if (control.Parent is TableLayoutPanel or FlowLayoutPanel)
            {
                Assert.True(
                    control.Left >= -1 && control.Right <= control.Parent.ClientSize.Width + 1,
                    $"{name}: '{control.Text}' ({control.GetType().Name}) runs from {control.Left} to {control.Right} in a box {control.Parent.ClientSize.Width} wide.");
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!IsSetVisible(child))
            {
                continue;
            }

            yield return child;
            foreach (var below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>
    /// Draws the window, and keeps the picture (its inside, without the title
    /// bar) as a PNG when the snapshot folder is set. It is drawn either way:
    /// a control that throws while painting should fail here, not on the
    /// server PC.
    /// </summary>
    private static void Save(Control window, string name)
    {
        using var whole = new Bitmap(window.Width, window.Height);
        window.DrawToBitmap(whole, new Rectangle(Point.Empty, window.Size));

        var folder = Environment.GetEnvironmentVariable(ViewSnapshot.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }
        var inside = window.RectangleToScreen(window.ClientRectangle);
        inside.Offset(-window.Left, -window.Top);
        using var picture = whole.Clone(inside, whole.PixelFormat);

        // A one-line text box of a window that is not on screen draws nothing
        // of its own, so what it holds is written in here: its text, dots for
        // a secret, or its placeholder in grey.
        using (var graphics = Graphics.FromImage(picture))
        {
            foreach (var box in Descendants(window).OfType<TextBox>().Where(box => !box.Multiline))
            {
                var area = window.RectangleToClient(box.RectangleToScreen(box.ClientRectangle));
                var (words, colour) = box.Text.Length == 0
                    ? (box.PlaceholderText, SystemColors.GrayText)
                    : (box.UseSystemPasswordChar ? new string('\u25CF', box.Text.Length) : box.Text, box.ForeColor);
                TextRenderer.DrawText(
                    graphics,
                    words,
                    box.Font,
                    area,
                    colour,
                    box.BackColor,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
        Directory.CreateDirectory(folder);
        picture.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
    }

    /// <summary>WinForms controls belong to a single-threaded apartment; the test runner's threads are not one.</summary>
    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Environment.GetEnvironmentVariable("TALLYWRAPPER_SNAPSHOT_SCALED") == "1")
                {
                    System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                }

                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
