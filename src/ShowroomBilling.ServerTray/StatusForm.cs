using System.Net.Http.Json;
using System.Text.Json;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.ServerTray;

public sealed class StatusForm : Form
{
    private static readonly Color PageBackground = Color.FromArgb(248, 250, 252);
    private static readonly Color CardBackground = Color.White;
    private static readonly Color BorderColor = Color.FromArgb(226, 232, 240);
    private static readonly Color Ink = Color.FromArgb(15, 23, 42);
    private static readonly Color Muted = Color.FromArgb(71, 85, 105);
    private static readonly Color Good = Color.FromArgb(22, 101, 52);
    private static readonly Color Warn = Color.FromArgb(146, 64, 14);
    private static readonly Color Bad = Color.FromArgb(185, 28, 28);

    private readonly ServerTrayOptions _options;
    private readonly ServerTrayActions _actions;
    private readonly Action _stopServiceAndExitTray;
    private readonly HttpClient _httpClient;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly Label _serviceStatus = StatusLabel();
    private readonly Label _apiStatus = StatusLabel();
    private readonly Label _databaseStatus = StatusLabel();
    private readonly Label _clientsStatus = StatusLabel();
    private readonly Label _serverUrl = DetailValue();
    private readonly Label _configRoot = DetailValue();
    private readonly Label _apiPath = DetailValue();
    private readonly TextBox _connectionString = new()
    {
        UseSystemPasswordChar = true,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 4, 0, 8)
    };
    private readonly ListBox _clients = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        HorizontalScrollbar = true,
        BorderStyle = BorderStyle.FixedSingle
    };

    private bool _refreshInFlight;

    public StatusForm(
        ServerTrayOptions options,
        ServerTrayActions actions,
        Action stopServiceAndExitTray)
    {
        _options = options;
        _actions = actions;
        _stopServiceAndExitTray = stopServiceAndExitTray;
        _httpClient = new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(5) };

        Text = "Tally Wrapper Server";
        Icon = AppIconProvider.CreateIcon();
        BackColor = PageBackground;
        Width = 920;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 620);

        Controls.Add(BuildContent());

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _actions.StatusChanged += HandleActionStatusChanged;
        Shown += (_, _) =>
        {
            _refreshTimer.Start();
            _ = RefreshAsync();
        };
    }

    private Control BuildContent()
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(18)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 6
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(root);

        root.Controls.Add(BuildHeader());
        root.Controls.Add(BuildStatusCards());
        root.Controls.Add(BuildServerActions());
        root.Controls.Add(BuildDatabaseCard());
        root.Controls.Add(BuildClientsCard());
        root.Controls.Add(BuildTrayActions());

        return scroll;
    }

    private Control BuildHeader()
    {
        var card = Card();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        card.Controls.Add(layout);

        var text = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1 };
        text.Controls.Add(new Label
        {
            Text = "Tally Wrapper Server",
            AutoSize = true,
            Font = new Font(UiFontFamily(), 18, FontStyle.Bold),
            ForeColor = Ink
        });
        text.Controls.Add(new Label
        {
            Text = "Tally-host service control, diagnostics, database maintenance, and workstation connection details.",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 4, 0, 0)
        });

        var refresh = PrimaryButton("Refresh", async (_, _) => await RefreshAsync());
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(refresh, 1, 0);

        return card;
    }

    private Control BuildStatusCards()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 12)
        };
        for (var i = 0; i < 4; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        grid.Controls.Add(StatusCard("API service", _serviceStatus), 0, 0);
        grid.Controls.Add(StatusCard("Local API", _apiStatus), 1, 0);
        grid.Controls.Add(StatusCard("Database", _databaseStatus), 2, 0);
        grid.Controls.Add(StatusCard("Workstations", _clientsStatus), 3, 0);
        return grid;
    }

    private Control BuildServerActions()
    {
        _serverUrl.Text = _actions.GetWorkstationUrl();
        _configRoot.Text = _options.ConfigRoot;
        _apiPath.Text = _options.ApiExecutablePath;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        body.Controls.Add(DetailRow("Workstation URL", _serverUrl));
        body.Controls.Add(DetailRow("Config root", _configRoot));
        body.Controls.Add(DetailRow("API executable", _apiPath));
        body.Controls.Add(ButtonRow(
            PrimaryButton("Install / Repair Server", (_, _) => _actions.InstallOrRepairServer(this)),
            Button("Start API Service", (_, _) => _actions.StartService(this)),
            Button("Stop API Service", (_, _) => _actions.StopService(this)),
            Button("Restart API Service", (_, _) => _actions.RestartService(this)),
            Button("Open Local Health", (_, _) => _actions.OpenLocalHealth()),
            Button("Copy Server URL", (_, _) => _actions.CopyServerUrl())));

        return Section("Server actions", "Everything from the tray menu is available here.", body);
    }

    private Control BuildDatabaseCard()
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        body.Controls.Add(new Label
        {
            Text = "Postgres connection string",
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font(UiFontFamily(), 8.5f, FontStyle.Bold)
        });
        body.Controls.Add(_connectionString);
        body.Controls.Add(ButtonRow(
            Button("Test DB", async (_, _) => await TestDatabaseAsync()),
            PrimaryButton("Save DB", async (_, _) => await SaveDatabaseAsync()),
            Button("Restart API Service", (_, _) => _actions.RestartService(this))));

        return Section("Database configuration", "Maintenance-token protected actions run against the local API.", body);
    }

    private Control BuildClientsCard()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 160,
            ColumnCount = 1,
            RowCount = 2
        };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(_clients, 0, 0);
        body.Controls.Add(ButtonRow(
            Button("Refresh Clients", async (_, _) => await RefreshClientsAsync()),
            Button("Open Logs", (_, _) => _actions.OpenLogs()),
            Button("Open Config Folder", (_, _) => _actions.OpenConfigRoot()),
            Button("Open Install Log", (_, _) => _actions.OpenInstallLog())), 0, 1);

        return Section("Connected clients", "Recently seen billing workstations from the API presence endpoint.", body);
    }

    private Control BuildTrayActions()
    {
        var body = ButtonRow(
            DangerButton("Exit Tray", (_, _) => _stopServiceAndExitTray()));
        return Section(
            "Tray shutdown",
            "Exiting the tray stops the API Windows Service first so the server shuts down cleanly.",
            body);
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
            RefreshServiceStatus();
            await RefreshApiStatusAsync();
            await RefreshClientsAsync();
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private void RefreshServiceStatus()
    {
        var text = _actions.GetServiceStatusText();
        var value = text.Replace("Service:", string.Empty).Trim();
        var color = value.Contains("Running", StringComparison.OrdinalIgnoreCase)
            ? Good
            : value.Contains("Stopped", StringComparison.OrdinalIgnoreCase)
                ? Bad
                : Warn;
        SetStatus(_serviceStatus, value, color);
    }

    private async Task RefreshApiStatusAsync()
    {
        try
        {
            using var live = await _httpClient.GetAsync("/api/health/live");
            SetStatus(
                _apiStatus,
                live.IsSuccessStatusCode ? "running" : $"HTTP {(int)live.StatusCode}",
                live.IsSuccessStatusCode ? Good : Warn);

            var runtime = await _httpClient.GetFromJsonAsync<RuntimeHealthResponse>("/api/runtime/health");
            if (runtime is null)
            {
                SetStatus(_databaseStatus, "unknown", Warn);
                return;
            }

            if (runtime.DatabaseHealthSkipped)
            {
                SetStatus(_databaseStatus, "idle", Muted);
                return;
            }

            SetStatus(
                _databaseStatus,
                runtime.DatabaseReachable ? "ready" : "not ready",
                runtime.DatabaseReachable ? Good : Bad);
        }
        catch (Exception ex)
        {
            SetStatus(_apiStatus, ShortError(ex.Message), Bad);
            SetStatus(_databaseStatus, "unknown", Warn);
        }
    }

    private async Task RefreshClientsAsync()
    {
        _clients.Items.Clear();
        try
        {
            var presence = await _httpClient.GetFromJsonAsync<ClientPresenceListResponse>("/api/clients/presence");
            var clients = presence?.Clients ?? [];
            SetStatus(_clientsStatus, clients.Count.ToString(), clients.Count > 0 ? Good : Warn);
            if (clients.Count == 0)
            {
                _clients.Items.Add("No active workstation clients.");
                return;
            }

            foreach (var client in clients)
            {
                _clients.Items.Add(
                    $"{client.CounterName} | {client.MachineName}\\{client.UserName} | {client.ConnectionMode} | {client.RemoteAddress} | {client.LastSeenAtUtc.LocalDateTime:HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            SetStatus(_clientsStatus, "unavailable", Bad);
            _clients.Items.Add(ShortError(ex.Message));
        }
    }

    private async Task TestDatabaseAsync()
    {
        var token = ReadMaintenanceToken();
        if (string.IsNullOrWhiteSpace(token)) return;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime/database/maintenance/test")
        {
            Content = JsonContent.Create(new TestDatabaseConfigurationRequest(_connectionString.Text))
        };
        request.Headers.Add(MaintenanceTokenConstants.HeaderName, token);
        await SendAndReportAsync(request, "Database test");
    }

    private async Task SaveDatabaseAsync()
    {
        var token = ReadMaintenanceToken();
        if (string.IsNullOrWhiteSpace(token)) return;

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/runtime/database/maintenance")
        {
            Content = JsonContent.Create(new UpdateDatabaseConfigurationRequest(_connectionString.Text))
        };
        request.Headers.Add(MaintenanceTokenConstants.HeaderName, token);
        await SendAndReportAsync(request, "Database save");
    }

    private async Task SendAndReportAsync(HttpRequestMessage request, string label)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var report = BuildOperationReport(label, response, body);
            CopyableMessageDialog.ShowMessage(
                this,
                "Tally Wrapper Server",
                report.Message,
                report.Icon);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            CopyableMessageDialog.ShowMessage(
                this,
                "Tally Wrapper Server",
                $"{label} failed: {ex.Message}",
                MessageBoxIcon.Error);
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
            "Tally Wrapper Server",
            $"Maintenance token was not found at {path}. Run Install / Repair Server first.",
            MessageBoxIcon.Error);
        return null;
    }

    private static OperationReport BuildOperationReport(
        string label,
        HttpResponseMessage response,
        string body)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new OperationReport(
                $"{label} failed: HTTP {(int)response.StatusCode}\r\n\r\n{FormatErrorBody(body)}",
                MessageBoxIcon.Error);
        }

        if (TryReadSuccessEnvelope(body, out var success, out var responseMessage))
        {
            var message = string.IsNullOrWhiteSpace(responseMessage)
                ? body
                : responseMessage;

            return success
                ? new OperationReport($"{label} succeeded.\r\n\r\n{message}", MessageBoxIcon.Information)
                : new OperationReport($"{label} failed.\r\n\r\n{message}", MessageBoxIcon.Error);
        }

        return new OperationReport($"{label} succeeded.\r\n\r\n{body}", MessageBoxIcon.Information);
    }

    private static string FormatErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var title = root.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString()
                : null;
            var detail = root.TryGetProperty("detail", out var detailElement)
                && detailElement.ValueKind == JsonValueKind.String
                ? detailElement.GetString()
                : null;
            var message = root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(detail))
            {
                return $"{title}: {detail}";
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                return detail;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body.
        }

        return body;
    }

    private static bool TryReadSuccessEnvelope(
        string body,
        out bool success,
        out string? message)
    {
        success = false;
        message = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var successElement)
                || successElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                return false;
            }

            success = successElement.GetBoolean();
            message = root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record OperationReport(string Message, MessageBoxIcon Icon);

    private void HandleActionStatusChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    private static Panel Card()
    {
        return new Panel
        {
            BackColor = CardBackground,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12),
            Dock = DockStyle.Top,
            AutoSize = true
        };
    }

    private static Control StatusCard(string title, Label value)
    {
        var card = Card();
        card.Margin = new Padding(0, 0, 10, 12);
        card.Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        layout.Controls.Add(new Label
        {
            Text = title.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = Muted,
            Font = new Font(UiFontFamily(), 8.5f, FontStyle.Bold)
        });
        layout.Controls.Add(value);
        card.Controls.Add(layout);
        return card;
    }

    private static Control Section(string title, string subtitle, Control body)
    {
        var card = Card();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        layout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = Ink,
            Font = new Font(UiFontFamily(), 12.5f, FontStyle.Bold)
        });
        layout.Controls.Add(new Label
        {
            Text = subtitle,
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 2, 0, 8)
        });
        layout.Controls.Add(body);
        card.Controls.Add(layout);
        return card;
    }

    private static Control DetailRow(string label, Label value)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 6)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Muted }, 0, 0);
        layout.Controls.Add(value, 1, 0);
        return layout;
    }

    private static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 4, 0, 0)
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Button PrimaryButton(string text, EventHandler onClick)
    {
        var button = Button(text, onClick);
        button.BackColor = Color.FromArgb(79, 70, 229);
        button.ForeColor = Color.White;
        return button;
    }

    private static Button DangerButton(string text, EventHandler onClick)
    {
        var button = Button(text, onClick);
        button.BackColor = Color.FromArgb(220, 38, 38);
        button.ForeColor = Color.White;
        button.Width = 190;
        return button;
    }

    private static Button Button(string text, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Width = 150,
            Height = 34,
            Margin = new Padding(0, 6, 8, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Ink
        };
        button.FlatAppearance.BorderColor = BorderColor;
        button.Click += onClick;
        return button;
    }

    private static Label StatusLabel() => new()
    {
        AutoSize = true,
        Text = "checking",
        ForeColor = Warn,
        Font = new Font(UiFontFamily(), 14, FontStyle.Bold),
        Margin = new Padding(0, 6, 0, 0)
    };

    private static Label DetailValue() => new()
    {
        AutoSize = true,
        ForeColor = Ink,
        Font = new Font("Consolas", 9),
        MaximumSize = new Size(640, 0)
    };

    private static void SetStatus(Label label, string text, Color color)
    {
        label.Text = text;
        label.ForeColor = color;
    }

    private static string ShortError(string message)
    {
        var singleLine = message.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= 80 ? singleLine : $"{singleLine[..77]}...";
    }

    private static FontFamily UiFontFamily() =>
        SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif;

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
}
