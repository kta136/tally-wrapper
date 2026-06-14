using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Services.ProcessSupervision;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class DatabaseSettingsViewModel : ObservableObject, IDatabaseConfigurationWorkflowHost
{
    private readonly DatabaseConfigurationWorkflow _databaseWorkflow;
    private readonly DesktopBootstrapOptions _bootstrapOptions;
    private readonly Action _restartApplication;
    private readonly Func<bool> _confirmConnectionModeRestart;
    private readonly Func<bool> _hasUnsavedSettingsEdits;
    private string _lastServerApiBaseUrl;

    public DatabaseSettingsViewModel(
        IRuntimeApiClient? runtimeApi = null,
        IHealthApiClient? healthApi = null,
        AdminTokenStore? adminTokenStore = null,
        IChildProcessSupervisor? childProcessSupervisor = null,
        DesktopBootstrapOptions? bootstrapOptions = null,
        Action? restartApplication = null,
        Func<bool>? confirmConnectionModeRestart = null,
        Func<bool>? hasUnsavedSettingsEdits = null)
    {
        _bootstrapOptions = bootstrapOptions ?? new DesktopBootstrapOptions();
        _restartApplication = restartApplication ?? RestartCurrentProcess;
        _confirmConnectionModeRestart = confirmConnectionModeRestart ?? ConfirmConnectionModeRestart;
        _hasUnsavedSettingsEdits = hasUnsavedSettingsEdits ?? (static () => false);
        _databaseWorkflow = new DatabaseConfigurationWorkflow(runtimeApi, healthApi, adminTokenStore, childProcessSupervisor, this);

        LoadDatabaseConfigCommand = new AsyncRelayCommand(LoadDatabaseConfigAsync, () => !IsDatabaseConfigBusy);
        TestDatabaseConnectionCommand = new AsyncRelayCommand(TestDatabaseConnectionAsync, CanUseDatabaseConfigCommands);
        SaveDatabaseConfigCommand = new AsyncRelayCommand(SaveDatabaseConfigAsync, CanUseDatabaseConfigCommands);
        RestartApiCommand = new AsyncRelayCommand(RestartApiAsync, () => CanRestartApi);
        SaveApiConnectionModeCommand = new AsyncRelayCommand(SaveApiConnectionModeAsync, CanSaveApiConnectionMode);
        TestServerConnectionCommand = new AsyncRelayCommand(TestServerConnectionAsync, CanUseServerUrlCommands);
        FindServerCommand = new AsyncRelayCommand(FindServerAsync, () => !IsFindingServer && !IsTestingServerConnection);

        _lastServerApiBaseUrl = PreferNonLocalhost(_bootstrapOptions.ServerApiBaseUrl, _bootstrapOptions.EffectiveApiBaseUrl);
        ApiConnectionMode = _bootstrapOptions.IsServerMode
            ? DesktopConnectionModes.Server
            : DesktopConnectionModes.LocalEmbedded;
        ServerApiBaseUrl = _lastServerApiBaseUrl;
        RefreshLocalEmbeddedDatabaseOverride();
    }

    public IAsyncRelayCommand LoadDatabaseConfigCommand { get; }
    public IAsyncRelayCommand TestDatabaseConnectionCommand { get; }
    public IAsyncRelayCommand SaveDatabaseConfigCommand { get; }
    public IAsyncRelayCommand RestartApiCommand { get; }
    public IAsyncRelayCommand SaveApiConnectionModeCommand { get; }
    public IAsyncRelayCommand TestServerConnectionCommand { get; }
    public IAsyncRelayCommand FindServerCommand { get; }

    public Func<CancellationToken, Task>? AdminUnlockHandler { get; set; }

    [ObservableProperty] private string databaseConnectionString = string.Empty;
    [ObservableProperty] private string databaseMaskedConnectionString = "—";
    [ObservableProperty] private string databaseConfigPath = "—";
    [ObservableProperty] private string databaseConfigStatus = string.Empty;
    [ObservableProperty] private string apiConnectionMode = DesktopConnectionModes.LocalEmbedded;
    [ObservableProperty] private string serverApiBaseUrl = string.Empty;
    [ObservableProperty] private string apiConnectionStatus = string.Empty;
    [ObservableProperty] private bool isTestingServerConnection;
    [ObservableProperty] private bool isFindingServer;
    [ObservableProperty] private bool isDatabaseConfigBusy;
    [ObservableProperty] private bool isTestingDatabaseConnection;
    [ObservableProperty] private bool isSavingDatabaseConfig;
    [ObservableProperty] private bool isRestartingApi;
    [ObservableProperty] private bool isLocalDatabaseOverridePresent;
    [ObservableProperty] private bool databaseConfigRequiresRestart;
    [ObservableProperty] private bool canBootstrapDatabaseWithoutAdmin;

    public IReadOnlyList<string> ApiConnectionModeOptions { get; } =
    [
        DesktopConnectionModes.Server,
        DesktopConnectionModes.LocalEmbedded
    ];

    public bool CanRestartApi => _databaseWorkflow.CanRestartApi;
    public string CurrentApiBaseUrl => _bootstrapOptions.EffectiveApiBaseUrl;
    public string DesktopBootstrapConfigPath => DesktopBootstrapLocalOverrideStore.ConfigPath;
    public bool IsRunningServerMode => _bootstrapOptions.IsServerMode;
    public bool IsDatabaseOverrideEditorEnabled => !IsRunningServerMode;
    public bool IsServerApiUrlEnabled =>
        string.Equals(ApiConnectionMode, DesktopConnectionModes.Server, StringComparison.OrdinalIgnoreCase);
    public string LocalEmbeddedApiBaseUrl => _bootstrapOptions.ApiBaseUrl;
    public string RunningConnectionModeText => _bootstrapOptions.IsServerMode ? "Server" : "LocalEmbedded";
    public string ApiConnectionModeSummary => _bootstrapOptions.IsServerMode
        ? $"Current mode: Server ({_bootstrapOptions.EffectiveApiBaseUrl})"
        : $"Current mode: Local embedded API ({_bootstrapOptions.EffectiveApiBaseUrl})";
    public string SelectedConnectionModeHelp => IsServerApiUrlEnabled
        ? "This workstation will call the API service on the Tally server."
        : $"This workstation will run its own embedded API at {_bootstrapOptions.ApiBaseUrl}.";
    public string DatabaseSetupModeText => CanBootstrapDatabaseWithoutAdmin
        ? "First-run setup is open. Paste the PostgreSQL connection string; it will be encrypted for this Windows user."
        : "Database changes are admin-protected after setup.";

    public string SaveDatabaseConfigButtonText => CanBootstrapDatabaseWithoutAdmin
        ? "Save and restart"
        : "Save override";

    partial void OnDatabaseConnectionStringChanged(string value) => NotifyDatabaseConfigCommandsChanged();

    partial void OnApiConnectionModeChanged(string value)
    {
        if (string.Equals(value, DesktopConnectionModes.Server, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(ServerApiBaseUrl) || IsLocalhostUrl(ServerApiBaseUrl))
            {
                ServerApiBaseUrl = _lastServerApiBaseUrl;
            }
        }
        else if (string.Equals(value, DesktopConnectionModes.LocalEmbedded, StringComparison.OrdinalIgnoreCase))
        {
            RefreshLocalEmbeddedDatabaseOverride();
            ApiConnectionStatus = $"LocalEmbedded selected. After restart Billing will use {_bootstrapOptions.ApiBaseUrl}.";
        }

        OnPropertyChanged(nameof(IsServerApiUrlEnabled));
        OnPropertyChanged(nameof(SelectedConnectionModeHelp));
        SaveApiConnectionModeCommand.NotifyCanExecuteChanged();
        TestServerConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnServerApiBaseUrlChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !IsLocalhostUrl(value))
        {
            _lastServerApiBaseUrl = value.Trim();
        }

        SaveApiConnectionModeCommand.NotifyCanExecuteChanged();
        TestServerConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDatabaseConfigBusyChanged(bool value) => NotifyDatabaseConfigCommandsChanged();
    partial void OnIsTestingDatabaseConnectionChanged(bool value) => NotifyDatabaseConfigCommandsChanged();
    partial void OnIsSavingDatabaseConfigChanged(bool value) => NotifyDatabaseConfigCommandsChanged();

    partial void OnIsTestingServerConnectionChanged(bool value)
    {
        TestServerConnectionCommand.NotifyCanExecuteChanged();
        FindServerCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFindingServerChanged(bool value)
    {
        FindServerCommand.NotifyCanExecuteChanged();
        TestServerConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsRestartingApiChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRestartApi));
        RestartApiCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanBootstrapDatabaseWithoutAdminChanged(bool value)
    {
        OnPropertyChanged(nameof(DatabaseSetupModeText));
        OnPropertyChanged(nameof(SaveDatabaseConfigButtonText));
    }

    public async Task LoadDatabaseConfigAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunningServerMode)
        {
            RefreshLocalEmbeddedDatabaseOverride();
            return;
        }

        await _databaseWorkflow.LoadAsync(cancellationToken);
    }

    private async Task TestDatabaseConnectionAsync(CancellationToken cancellationToken)
        => await _databaseWorkflow.TestAsync(cancellationToken);

    private async Task SaveDatabaseConfigAsync(CancellationToken cancellationToken)
        => await _databaseWorkflow.SaveAsync(cancellationToken);

    private async Task RestartApiAsync(CancellationToken cancellationToken)
        => await _databaseWorkflow.RestartApiAsync(cancellationToken);

    private void NotifyDatabaseConfigCommandsChanged()
    {
        LoadDatabaseConfigCommand.NotifyCanExecuteChanged();
        TestDatabaseConnectionCommand.NotifyCanExecuteChanged();
        SaveDatabaseConfigCommand.NotifyCanExecuteChanged();
        RestartApiCommand.NotifyCanExecuteChanged();
    }

    private bool CanUseDatabaseConfigCommands() =>
        IsDatabaseOverrideEditorEnabled && _databaseWorkflow.CanUseCommands();

    private bool CanUseServerUrlCommands() =>
        IsServerApiUrlEnabled
        && !IsTestingServerConnection
        && !IsFindingServer
        && Uri.TryCreate(ServerApiBaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(uri.Host);

    private bool CanSaveApiConnectionMode()
    {
        if (string.IsNullOrWhiteSpace(ApiConnectionMode))
        {
            return false;
        }

        if (string.Equals(ApiConnectionMode, DesktopConnectionModes.Server, StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(ServerApiBaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && !string.IsNullOrWhiteSpace(uri.Host);
        }

        return string.Equals(ApiConnectionMode, DesktopConnectionModes.LocalEmbedded, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SaveApiConnectionModeAsync(CancellationToken cancellationToken)
    {
        if (!CanSaveApiConnectionMode())
        {
            ApiConnectionStatus = "Enter a valid server URL, for example http://192.168.1.50:5107.";
            return;
        }

        var normalizedMode = string.Equals(ApiConnectionMode, DesktopConnectionModes.Server, StringComparison.OrdinalIgnoreCase)
            ? DesktopConnectionModes.Server
            : DesktopConnectionModes.LocalEmbedded;
        var normalizedServerUrl = NormalizeApiBaseUrl(ServerApiBaseUrl);
        if (!IsLocalhostUrl(normalizedServerUrl))
        {
            _lastServerApiBaseUrl = normalizedServerUrl;
        }

        if (_hasUnsavedSettingsEdits() && !_confirmConnectionModeRestart())
        {
            ApiConnectionStatus = "Connection mode save cancelled. Save or discard settings edits before restarting.";
            return;
        }

        await DesktopBootstrapLocalOverrideStore.SaveAsync(
            normalizedMode,
            normalizedServerUrl,
            cancellationToken);

        ApiConnectionStatus = $"Saved {normalizedMode}. Restarting Billing...";
        _restartApplication();
    }

    private async Task TestServerConnectionAsync(CancellationToken cancellationToken)
    {
        if (!CanUseServerUrlCommands())
        {
            ApiConnectionStatus = "Enter a valid server URL, for example http://192.168.1.13:5107.";
            return;
        }

        IsTestingServerConnection = true;
        ApiConnectionStatus = "Testing server connection...";
        try
        {
            var result = await ProbeServerAsync(NormalizeApiBaseUrl(ServerApiBaseUrl), cancellationToken);
            ApiConnectionStatus = result.StatusMessage;
        }
        finally
        {
            IsTestingServerConnection = false;
        }
    }

    private async Task FindServerAsync(CancellationToken cancellationToken)
    {
        IsFindingServer = true;
        ApiConnectionStatus = "Scanning the local network for Tally Wrapper server...";
        try
        {
            var found = await FindServerCandidatesAsync(cancellationToken);
            if (found.Count == 0)
            {
                ApiConnectionStatus = "No Tally Wrapper server found on this subnet. Enter the server URL manually.";
                return;
            }

            ServerApiBaseUrl = found[0];
            ApiConnectionMode = DesktopConnectionModes.Server;
            ApiConnectionStatus = found.Count == 1
                ? $"Found server: {found[0]}"
                : $"Found {found.Count} servers. Filled {found[0]}.";
        }
        catch (OperationCanceledException)
        {
            ApiConnectionStatus = "Server scan cancelled.";
        }
        catch (Exception ex)
        {
            ApiConnectionStatus = $"Server scan failed: {ex.Message}";
        }
        finally
        {
            IsFindingServer = false;
        }
    }

    private static string NormalizeApiBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return value.Trim();
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private void RefreshLocalEmbeddedDatabaseOverride()
    {
        var snapshot = DesktopLocalDatabaseOverrideStore.Load();
        DatabaseConfigPath = snapshot.ConfigPath;
        IsLocalDatabaseOverridePresent = snapshot.Exists;
        DatabaseConfigRequiresRestart = false;
        CanBootstrapDatabaseWithoutAdmin = false;
        if (snapshot.ConnectionString is { Length: > 0 } connectionString)
        {
            DatabaseConnectionString = connectionString;
            DatabaseMaskedConnectionString = DesktopLocalDatabaseOverrideStore.MaskConnectionString(connectionString);
            DatabaseConfigStatus = IsRunningServerMode
                ? "Loaded this workstation's LocalEmbedded fallback DB override. Server DB is configured from the server tray."
                : "Loaded local embedded DB override.";
            return;
        }

        DatabaseMaskedConnectionString = "—";
        if (IsRunningServerMode)
        {
            DatabaseConfigStatus = "No LocalEmbedded fallback DB override found on this workstation. Server DB is configured from the server tray.";
        }
    }

    private static async Task<ServerProbeResult> ProbeServerAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(6)
        };

        try
        {
            using var live = await client.GetAsync("/api/health/live", cancellationToken);
            if (!live.IsSuccessStatusCode)
            {
                return new ServerProbeResult($"Server responded on {baseUrl}, but live health returned HTTP {(int)live.StatusCode}.");
            }

            try
            {
                using var ready = await client.GetAsync("/api/health/ready", cancellationToken);
                if (ready.IsSuccessStatusCode)
                {
                    return new ServerProbeResult($"Server reachable and DB ready: {baseUrl}");
                }

                var body = await ready.Content.ReadAsStringAsync(cancellationToken);
                return new ServerProbeResult($"Server reachable, DB not ready: HTTP {(int)ready.StatusCode}. {SummarizeHealthBody(body)}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new ServerProbeResult($"Server reachable, but readiness check failed: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ServerProbeResult($"Cannot reach server at {baseUrl}: {ex.Message}");
        }
    }

    private static async Task<IReadOnlyList<string>> FindServerCandidatesAsync(CancellationToken cancellationToken)
    {
        var addresses = GetCandidateAddresses().Take(254).ToArray();
        var found = new List<string>();
        using var gate = new SemaphoreSlim(32);
        var tasks = addresses.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var url = $"http://{address}:5107";
                if (await LooksLikeShowroomServerAsync(url, cancellationToken))
                {
                    lock (found)
                    {
                        found.Add(url);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return found.OrderBy(static url => url, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<bool> LooksLikeShowroomServerAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMilliseconds(900) };
            using var live = await client.GetAsync("/api/health/live", cancellationToken);
            if (!live.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await live.Content.ReadAsStringAsync(cancellationToken);
            return body.Contains("Tally Wrapper", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetCandidateAddresses()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(unicast.Address))
                {
                    continue;
                }

                var bytes = unicast.Address.GetAddressBytes();
                for (var host = 1; host <= 254; host++)
                {
                    if (host == bytes[3])
                    {
                        continue;
                    }

                    yield return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{host}";
                }
            }
        }
    }

    private static string SummarizeHealthBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No response body.";
        }

        return body.Length <= 180 ? body : string.Concat(body.AsSpan(0, 180), "...");
    }

    private static bool IsLocalhostUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static string PreferNonLocalhost(string primary, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary) && !IsLocalhostUrl(primary))
        {
            return primary.Trim();
        }

        return !string.IsNullOrWhiteSpace(fallback) && !IsLocalhostUrl(fallback)
            ? fallback.Trim()
            : primary.Trim();
    }

    private static void RestartCurrentProcess()
    {
        var executable = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true
            });
        }

        System.Windows.Application.Current.Shutdown();
    }

    private static bool ConfirmConnectionModeRestart()
    {
        var result = MessageBox.Show(
            "Billing must restart to change the API connection mode. Unsaved Settings edits will be lost. Continue?",
            "Restart Billing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }
}

internal sealed record ServerProbeResult(string StatusMessage);
