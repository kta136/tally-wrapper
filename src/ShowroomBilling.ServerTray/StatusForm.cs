using System.Diagnostics;
using System.Net.Http.Json;
using System.ServiceProcess;
using ShowroomBilling.Contracts.Clients;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.ServerTray;

public sealed class StatusForm : Form
{
    private readonly ServerTrayOptions _options;
    private readonly HttpClient _httpClient;
    private readonly Label _serviceStatus = new() { AutoSize = true };
    private readonly Label _apiStatus = new() { AutoSize = true };
    private readonly Label _databaseStatus = new() { AutoSize = true };
    private readonly Label _clientsStatus = new() { AutoSize = true };
    private readonly TextBox _connectionString = new()
    {
        Width = 580,
        UseSystemPasswordChar = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right
    };
    private readonly ListBox _clients = new()
    {
        Width = 580,
        Height = 100,
        Anchor = AnchorStyles.Left | AnchorStyles.Right
    };

    public StatusForm(ServerTrayOptions options)
    {
        _options = options;
        _httpClient = new HttpClient { BaseAddress = new Uri(options.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(5) };

        Text = "Showroom Billing Server";
        Width = 680;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(620, 420);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 8
        };
        Controls.Add(root);

        root.Controls.Add(Header("Status"));
        root.Controls.Add(Stack(_serviceStatus, _apiStatus, _databaseStatus, _clientsStatus));
        root.Controls.Add(Header("Database Configuration"));
        root.Controls.Add(_connectionString);
        root.Controls.Add(ButtonRow(
            Button("Test DB", async (_, _) => await TestDatabaseAsync()),
            Button("Save DB", async (_, _) => await SaveDatabaseAsync()),
            Button("Restart Service", (_, _) => RestartService())));
        root.Controls.Add(Header("Connected Clients"));
        root.Controls.Add(_clients);
        root.Controls.Add(ButtonRow(
            Button("Refresh", async (_, _) => await RefreshAsync()),
            Button("Open Logs", (_, _) => OpenPath(_options.LogsPath)),
            Button("Copy URL", (_, _) => Clipboard.SetText(ServerUrlHelper.GetWorkstationApiBaseUrl(_options.ApiBaseUrl)))));

        Shown += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        RefreshServiceStatus();
        await RefreshApiStatusAsync();
        await RefreshClientsAsync();
    }

    private void RefreshServiceStatus()
    {
        try
        {
            using var service = new ServiceController(_options.ServiceName);
            _serviceStatus.Text = $"Service: {service.Status}";
        }
        catch (Exception ex)
        {
            _serviceStatus.Text = $"Service: unavailable ({ex.Message})";
        }
    }

    private async Task RefreshApiStatusAsync()
    {
        try
        {
            using var live = await _httpClient.GetAsync("/api/health/live");
            _apiStatus.Text = live.IsSuccessStatusCode ? "API: running" : $"API: HTTP {(int)live.StatusCode}";

            var runtime = await _httpClient.GetFromJsonAsync<RuntimeHealthResponse>("/api/runtime/health");
            _databaseStatus.Text = runtime is null
                ? "Database: unknown"
                : $"Database: {(runtime.DatabaseReachable ? "ready" : "not ready")} - {runtime.Message}";
        }
        catch (Exception ex)
        {
            _apiStatus.Text = $"API: unavailable ({ex.Message})";
            _databaseStatus.Text = "Database: unknown";
        }
    }

    private async Task RefreshClientsAsync()
    {
        _clients.Items.Clear();
        try
        {
            var presence = await _httpClient.GetFromJsonAsync<ClientPresenceListResponse>("/api/clients/presence");
            var clients = presence?.Clients ?? [];
            _clientsStatus.Text = $"Clients: {clients.Count}";
            foreach (var client in clients)
            {
                _clients.Items.Add(
                    $"{client.CounterName} | {client.MachineName}\\{client.UserName} | {client.ConnectionMode} | {client.RemoteAddress} | {client.LastSeenAtUtc.LocalDateTime:HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            _clientsStatus.Text = $"Clients: unavailable ({ex.Message})";
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
            MessageBox.Show(
                response.IsSuccessStatusCode ? $"{label} succeeded.\n\n{body}" : $"{label} failed: HTTP {(int)response.StatusCode}\n\n{body}",
                "Showroom Billing Server",
                MessageBoxButtons.OK,
                response.IsSuccessStatusCode ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{label} failed: {ex.Message}", "Showroom Billing Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? ReadMaintenanceToken()
    {
        var path = Path.Combine(_options.ConfigRoot, MaintenanceTokenConstants.FileName);
        if (File.Exists(path))
        {
            return File.ReadAllText(path).Trim();
        }

        MessageBox.Show(
            $"Maintenance token was not found at {path}. Run the server service installer first.",
            "Showroom Billing Server",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        return null;
    }

    private void RestartService()
    {
        try
        {
            using var service = new ServiceController(_options.ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            RefreshServiceStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Service restart failed: {ex.Message}\n\nRun the tray as Administrator or grant service-control rights to this user.",
                "Showroom Billing Server",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
        Padding = new Padding(0, 8, 0, 3)
    };

    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Button Button(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 6, 8, 6) };
        button.Click += onClick;
        return button;
    }

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }

        base.Dispose(disposing);
    }
}
