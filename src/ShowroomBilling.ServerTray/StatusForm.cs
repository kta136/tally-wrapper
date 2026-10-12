using System.Net.Http.Json;
using System.Text.Json;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.ServerTray;

/// <summary>
/// The server dashboard. It opens on one sentence, whether the counters can
/// bill, and under it the three things that sentence depends on, each with
/// the button that mends it. What it says is worked out in
/// <see cref="DashboardState"/>; this class lays it out and draws it.
/// </summary>
public sealed class StatusForm : Form
{
    private readonly ServerTrayOptions _options;
    private readonly ServerTrayActions _actions;
    private readonly Action _stopServiceAndExitTray;
    private readonly HttpClient _httpClient;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly StatusDot _verdictDot = new() { Anchor = AnchorStyles.Left, Margin = new Padding(0, 2, 12, 0) };
    private readonly TextLabel _verdict = new(TextRole.Title, Theme.Ink);
    private readonly TextLabel _detail = new(TextRole.Body, Theme.InkMuted, wrap: true) { Margin = new Padding(0, 4, 0, 0) };

    private readonly FlatButton _install = new("Install", ButtonKind.Primary, small: true);
    private readonly FlatButton _start = new("Start", ButtonKind.Primary, small: true);
    private readonly FlatButton _restart = new("Restart", small: true);
    private readonly FlatButton _stop = new("Stop", small: true);
    private readonly HealthRow _service;
    private readonly HealthRow _server = new("Server", hasActions: false);
    private readonly HealthRow _database = new("Database", hasActions: false);

    private readonly FieldBox _address = new(TextRole.Mono, readOnly: true);
    private readonly FlatButton _copyAddress = new("Copy");
    private readonly Chip _countersChip = new() { Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0) };
    private readonly CounterTable _counters = new();
    private readonly FieldBox _connection = new(TextRole.Mono);
    private readonly FlatButton _testConnection = new("Test") { Enabled = false };
    private readonly FlatButton _saveConnection = new("Save", ButtonKind.Primary) { Enabled = false };

    private bool _refreshInFlight;
    private ServiceAction? _shownServiceAction;

    public StatusForm(
        ServerTrayOptions options,
        ServerTrayActions actions,
        Action stopServiceAndExitTray)
    {
        _options = options;
        _actions = actions;
        _stopServiceAndExitTray = stopServiceAndExitTray;
        _httpClient = new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        _service = new HealthRow("Windows service", hasActions: true);

        // Every size below is written for 100 % scaling. The scale is declared
        // last, with layout held back until then, so that the first layout
        // scales the window and everything in it together. Declared first, it
        // would scale an empty window and leave what is added afterwards as it is.
        SuspendLayout();
        Text = ServerTrayOptions.ProductName;
        Icon = AppIconProvider.CreateIcon();
        BackColor = Theme.Page;
        StartPosition = FormStartPosition.CenterScreen;

        // The foot is added last so that it is docked first and the page takes what is left.
        Controls.Add(BuildPage());
        Controls.Add(BuildFoot());
        WireActions();
        Render(DashboardState.Checking);

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 830);
        MinimumSize = new Size(700, 480);
        ResumeLayout(performLayout: false);
        PerformLayout();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _actions.StatusChanged += HandleActionStatusChanged;
        Shown += (_, _) =>
        {
            _refreshTimer.Start();
            _ = RefreshAsync();
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // A server PC often has a small screen: fit it, and let the page scroll.
        var area = Screen.FromControl(this).WorkingArea;
        if (Height > area.Height)
        {
            Height = area.Height;
            CenterToScreen();
        }
    }

    private Control BuildPage()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(24, 22, 24, 0),
            BackColor = Theme.Page,
        };

        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Page,
        };
        column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column.Controls.Add(BuildHeader());
        column.Controls.Add(BuildHealthCard());
        column.Controls.Add(BuildAddressCard());
        column.Controls.Add(BuildCountersCard());
        column.Controls.Add(BuildDatabaseCard());
        page.Controls.Add(column);
        return page;
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Margin = new Padding(0, 0, 0, 18),
            BackColor = Theme.Page,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var check = new FlatButton("Check again") { Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(12, 2, 0, 0) };
        check.Click += async (_, _) => await RefreshAsync();

        header.Controls.Add(_verdictDot, 0, 0);
        header.Controls.Add(_verdict, 1, 0);
        header.Controls.Add(_detail, 1, 1);
        header.Controls.Add(check, 2, 0);
        header.SetRowSpan(check, 2);
        return header;
    }

    private Control BuildHealthCard()
    {
        // The lines run edge to edge with a hairline between them; the padding
        // above and below keeps them clear of the card's corners.
        var card = new Card { Padding = new Padding(1, 6, 1, 6) };
        card.Controls.Add(_service);
        card.Controls.Add(Hairline());
        card.Controls.Add(_server);
        card.Controls.Add(Hairline());
        card.Controls.Add(_database);
        return card;
    }

    private Control BuildAddressCard()
    {
        var card = new Card { ColumnCount = 3 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var words = Stack(
            new TextLabel(TextRole.BodyStrong, Theme.Ink, "Address for the counters"),
            new TextLabel(TextRole.Small, Theme.InkSoft, "Enter it in Settings → Server on each counter.", wrap: true)
            {
                Margin = new Padding(0, 2, 0, 0),
            });
        words.Margin = new Padding(0, 0, 16, 0);

        _address.Input.Text = _actions.GetWorkstationUrl();
        card.Controls.Add(words, 0, 0);
        card.Controls.Add(_address, 1, 0);
        card.Controls.Add(_copyAddress, 2, 0);
        _copyAddress.Anchor = AnchorStyles.Right;
        return card;
    }

    private Control BuildCountersCard()
    {
        var card = new Card { Padding = new Padding(1, 14, 1, 6) };

        var heading = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(15, 0, 15, 12),
            BackColor = Theme.Panel,
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(
            new TextLabel(TextRole.BodyStrong, Theme.Ink, "Counters connected") { Anchor = AnchorStyles.Left },
            0,
            0);
        heading.Controls.Add(_countersChip, 1, 0);

        card.Controls.Add(heading);
        card.Controls.Add(_counters);
        return card;
    }

    private Control BuildDatabaseCard()
    {
        var card = new Card { ColumnCount = 3 };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var heading = new TextLabel(TextRole.BodyStrong, Theme.Ink, "Database connection");
        var context = new TextLabel(
            TextRole.Small,
            Theme.InkSoft,
            "It is kept on this PC and never shown. To change it, paste the new connection string, test it, then save. "
            + "The server starts using a saved one when the Windows service restarts.",
            wrap: true)
        {
            Margin = new Padding(0, 2, 0, 12),
        };

        _connection.Input.UseSystemPasswordChar = true;
        _connection.Input.PlaceholderText = "Host=…;Port=…;Database=…;Username=…;Password=…";

        card.Controls.Add(heading, 0, 0);
        card.SetColumnSpan(heading, 3);
        card.Controls.Add(context, 0, 1);
        card.SetColumnSpan(context, 3);
        card.Controls.Add(_connection, 0, 2);
        card.Controls.Add(_testConnection, 1, 2);
        card.Controls.Add(_saveConnection, 2, 2);
        return card;
    }

    private Control BuildFoot()
    {
        // The words of the first link start where the cards do, and the red
        // button ends where they end.
        var foot = new FootBand { Padding = new Padding(14, 1, 24, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Theme.Sunken };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
            BackColor = Theme.Sunken,
        };
        links.Controls.Add(Link("Logs", () => _actions.OpenLogs()));
        links.Controls.Add(Link("Data folder", () => _actions.OpenConfigRoot()));
        links.Controls.Add(Link("Install log", () => _actions.OpenInstallLog()));
        links.Controls.Add(Link("Repair installation…", () => _actions.InstallOrRepairServer(this)));

        var stop = new FlatButton("Stop server and exit", ButtonKind.Danger) { Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        stop.Click += (_, _) => _stopServiceAndExitTray();

        layout.Controls.Add(links, 0, 0);
        layout.Controls.Add(stop, 2, 0);
        foot.Controls.Add(layout);
        return foot;
    }

    private void WireActions()
    {
        _install.Click += (_, _) => _actions.InstallOrRepairServer(this);
        _start.Click += (_, _) => WhileWaiting(() => _actions.StartService(this));
        _restart.Click += (_, _) => WhileWaiting(() => _actions.RestartService(this));
        _stop.Click += (_, _) => WhileWaiting(() => _actions.StopService(this));

        _copyAddress.Click += (_, _) =>
        {
            _actions.CopyServerUrl();
            _copyAddress.Text = "Copied";
        };

        _connection.Input.TextChanged += (_, _) =>
        {
            var something = !string.IsNullOrWhiteSpace(_connection.Input.Text);
            _testConnection.Enabled = something;
            _saveConnection.Enabled = something;
        };
        _testConnection.Click += async (_, _) => await TestDatabaseAsync();
        _saveConnection.Click += async (_, _) => await SaveDatabaseAsync();
    }

    /// <summary>Starting or stopping a service takes seconds, and the window waits for it.</summary>
    private void WhileWaiting(Action action)
    {
        UseWaitCursor = true;
        try
        {
            action();
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshInFlight)
        {
            return;
        }

        _refreshInFlight = true;
        try
        {
            var state = await ReadStateAsync();
            if (!IsDisposed)
            {
                Render(state);
            }
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task<DashboardState> ReadStateAsync()
    {
        var service = _actions.GetServiceStatus();
        string? serverProblem = null;
        RuntimeHealthResponse? runtime = null;
        IReadOnlyList<ClientPresenceResponse>? counters = null;
        string? countersProblem = null;

        try
        {
            using var live = await _httpClient.GetAsync("/api/health/live");
            if (!live.IsSuccessStatusCode)
            {
                serverProblem = $"HTTP {(int)live.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            serverProblem = ShortError(ex.Message);
        }

        if (serverProblem is null)
        {
            try
            {
                runtime = await _httpClient.GetFromJsonAsync<RuntimeHealthResponse>("/api/runtime/health");
            }
            catch (Exception)
            {
                // Left null: the dashboard says the database was not reported on.
            }

            try
            {
                var presence = await _httpClient.GetFromJsonAsync<ClientPresenceListResponse>("/api/clients/presence");
                counters = presence?.Clients ?? [];
            }
            catch (Exception ex)
            {
                countersProblem = ShortError(ex.Message);
            }
        }

        return DashboardState.From(service, serverProblem, runtime, counters, countersProblem, DateTimeOffset.Now);
    }

    /// <summary>Draws a state. The checks call it every 15 seconds; a test calls it with states of its own.</summary>
    internal void Render(DashboardState state)
    {
        var page = Controls[0];
        page.SuspendLayout();
        try
        {
            _verdictDot.Tone = state.Tone;
            _verdict.Text = state.Verdict;
            _detail.Text = state.Detail;

            _service.Set(state.Service);
            _server.Set(state.Server);
            _database.Set(state.Database);
            if (_shownServiceAction != state.ServiceAction)
            {
                _shownServiceAction = state.ServiceAction;
                _service.SetActions(state.ServiceAction switch
                {
                    ServiceAction.Install => [_install],
                    ServiceAction.Start => [_start],
                    ServiceAction.RestartOrStop => [_restart, _stop],
                    _ => [],
                });
            }

            _countersChip.Tone = state.CountersTone;
            _countersChip.Text = state.CountersChip;
            _counters.SetRows(state.Counters, state.CountersEmptyText);
            _copyAddress.Text = "Copy";
        }
        finally
        {
            page.ResumeLayout(performLayout: true);
        }
    }

    private async Task TestDatabaseAsync()
    {
        var token = ReadMaintenanceToken();
        if (string.IsNullOrWhiteSpace(token)) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime/database/maintenance/test")
        {
            Content = JsonContent.Create(new TestDatabaseConfigurationRequest(_connection.Input.Text))
        };
        request.Headers.Add(MaintenanceTokenConstants.HeaderName, token);
        await SendAndReportAsync(request, "Connection test");
    }

    private async Task SaveDatabaseAsync()
    {
        var token = ReadMaintenanceToken();
        if (string.IsNullOrWhiteSpace(token)) return;

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/runtime/database/maintenance")
        {
            Content = JsonContent.Create(new UpdateDatabaseConfigurationRequest(_connection.Input.Text))
        };
        request.Headers.Add(MaintenanceTokenConstants.HeaderName, token);
        if (await SendAndReportAsync(request, "Save"))
        {
            // It is stored now; nothing is gained by keeping a secret in the box.
            _connection.Input.Clear();
        }
    }

    /// <returns>Whether the server said it had done what was asked.</returns>
    private async Task<bool> SendAndReportAsync(HttpRequestMessage request, string label)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var report = BuildOperationReport(label, response, body);
            CopyableMessageDialog.ShowMessage(this, report.Message, report.Icon);
            await RefreshAsync();
            return report.Icon != MessageBoxIcon.Error;
        }
        catch (Exception ex)
        {
            CopyableMessageDialog.ShowMessage(this, $"{label} failed.\n\n{ex.Message}", MessageBoxIcon.Error);
            return false;
        }
    }

    private string? ReadMaintenanceToken()
    {
        var path = Path.Combine(_options.ConfigRoot, MaintenanceTokenConstants.FileName);
        if (File.Exists(path))
        {
            return File.ReadAllText(path).Trim();
        }

        CopyableMessageDialog.ShowMessage(
            this,
            $"This PC's maintenance token is missing.\n\nIt was expected at {path}. Choose Repair installation to create it again.",
            MessageBoxIcon.Error);
        return null;
    }

    /// <summary>
    /// Turns an API answer into what the operator is shown. A maintenance call
    /// answers with <c>{ success, message }</c>; a saved connection answers
    /// with what was stored; a refusal answers with ProblemDetails
    /// (<c>title</c> / <c>detail</c>). Anything else is shown as it came.
    /// </summary>
    internal static OperationReport BuildOperationReport(
        string label,
        HttpResponseMessage response,
        string body)
    {
        var parsed = ParseBody(body);
        if (!response.IsSuccessStatusCode)
        {
            return new OperationReport(
                $"{label} failed: HTTP {(int)response.StatusCode}\r\n\r\n{DescribeError(parsed, body)}",
                MessageBoxIcon.Error);
        }

        if (parsed?.Success is { } success)
        {
            var message = string.IsNullOrWhiteSpace(parsed.Message) ? body : parsed.Message;
            return success
                ? new OperationReport($"{label} succeeded.\r\n\r\n{message}", MessageBoxIcon.Information)
                : new OperationReport($"{label} failed.\r\n\r\n{message}", MessageBoxIcon.Error);
        }

        if (!string.IsNullOrWhiteSpace(parsed?.MaskedConnectionString))
        {
            var next = parsed.RequiresApiRestart == true
                ? "\r\n\r\nThe server starts using it when the Windows service restarts."
                : string.Empty;
            return new OperationReport(
                $"{label} succeeded.\r\n\r\nStored: {parsed.MaskedConnectionString}{next}",
                MessageBoxIcon.Information);
        }

        return new OperationReport($"{label} succeeded.\r\n\r\n{body}", MessageBoxIcon.Information);
    }

    private static string DescribeError(ResponseBody? parsed, string body)
    {
        var title = parsed?.Title;
        var detail = parsed?.Detail;
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(detail)) return $"{title}: {detail}";
        if (!string.IsNullOrWhiteSpace(detail)) return detail;
        if (!string.IsNullOrWhiteSpace(title)) return title;
        if (!string.IsNullOrWhiteSpace(parsed?.Message)) return parsed.Message;
        return body;
    }

    private static ResponseBody? ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ResponseBody>(body, WebJson);
        }
        catch (JsonException)
        {
            // Not JSON, or not an object of this shape: the raw body is shown instead.
            return null;
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>The fields of the answer shapes the tray reads; each call sends only some of them.</summary>
    private sealed record ResponseBody(
        bool? Success,
        string? Message,
        string? Title,
        string? Detail,
        string? MaskedConnectionString,
        bool? RequiresApiRestart);

    internal sealed record OperationReport(string Message, MessageBoxIcon Icon);

    private void HandleActionStatusChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    private static Panel Hairline() => new()
    {
        Height = 1,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        BackColor = Theme.Divider,
    };

    /// <summary>Controls one under the other, as wide as the widest.</summary>
    private static TableLayoutPanel Stack(params Control[] controls)
    {
        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.Controls.AddRange(controls);
        return stack;
    }

    private static FlatButton Link(string text, Action onClick)
    {
        var link = new FlatButton(text, ButtonKind.Text, small: true) { Margin = new Padding(0, 0, 2, 0) };
        link.Click += (_, _) => onClick();
        return link;
    }

    private static string ShortError(string message)
    {
        var singleLine = message.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 80 ? singleLine : $"{singleLine[..77]}...";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _actions.StatusChanged -= HandleActionStatusChanged;
            _refreshTimer.Dispose();
            _httpClient.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// One line of the health card: a dot, what it is, a sentence about it,
    /// the same in a chip, and the buttons that act on it, if it has any.
    /// </summary>
    private sealed class HealthRow : TableLayoutPanel
    {
        private readonly StatusDot _dot = new() { Margin = new Padding(0, 4, 12, 0) };
        private readonly TextLabel _description = new(TextRole.Small, Theme.InkSoft, wrap: true) { Margin = new Padding(0, 2, 0, 0) };
        private readonly Chip _chip = new() { Anchor = AnchorStyles.Right, Margin = new Padding(12, 0, 0, 0) };
        private readonly FlowLayoutPanel? _actions;

        public HealthRow(string title, bool hasActions)
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            Padding = new Padding(15, 10, 15, 10);
            BackColor = Theme.Panel;
            ColumnCount = 4;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Controls.Add(_dot, 0, 0);
            Controls.Add(new TextLabel(TextRole.BodyStrong, Theme.Ink, title), 1, 0);
            Controls.Add(_description, 1, 1);
            Controls.Add(_chip, 2, 0);
            SetRowSpan(_chip, 2);

            if (hasActions)
            {
                _actions = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    WrapContents = false,
                    Anchor = AnchorStyles.Right,
                    // No margin of its own: with no button in it, it must take no room.
                    Margin = Padding.Empty,
                    BackColor = Theme.Panel,
                };
                Controls.Add(_actions, 3, 0);
                SetRowSpan(_actions, 2);
            }
        }

        /// <summary>
        /// The buttons this line offers now. They are put in and taken out,
        /// not shown and hidden: hiding a button in a window that is not on
        /// screen leaves its space behind.
        /// </summary>
        public void SetActions(Control[] buttons)
        {
            if (_actions is null)
            {
                return;
            }

            _actions.SuspendLayout();
            _actions.Controls.Clear();
            _actions.Controls.AddRange(buttons);
            _actions.ResumeLayout(performLayout: true);
        }

        public void Set(HealthLine line)
        {
            _dot.Tone = line.Tone;
            _description.Text = line.Description;
            _chip.Tone = line.Tone;
            _chip.Text = line.Chip;
        }
    }
}
