using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Server: which Tally Wrapper server this workstation works
/// against. The address is the only connection setting a workstation holds;
/// the server's database is set from the tray on the server itself.
/// </summary>
public partial class ServerSettingsViewModel : ObservableObject
{
    private const int ServerPort = 5107;

    private readonly DesktopBootstrapOptions _bootstrapOptions;
    private readonly Action _restartApplication;
    private readonly Func<bool> _hasUnsavedSettingsEdits;
    private readonly Func<string, CancellationToken, Task> _saveServerAddress;

    public ServerSettingsViewModel(
        DesktopBootstrapOptions? bootstrapOptions = null,
        Action? restartApplication = null,
        Func<bool>? hasUnsavedSettingsEdits = null,
        Func<string, CancellationToken, Task>? saveServerAddress = null)
    {
        _bootstrapOptions = bootstrapOptions ?? new DesktopBootstrapOptions();
        _restartApplication = restartApplication ?? AppRestart.Restart;
        _hasUnsavedSettingsEdits = hasUnsavedSettingsEdits ?? (static () => false);
        _saveServerAddress = saveServerAddress ?? DesktopBootstrapLocalOverrideStore.SaveAsync;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsServerAddressValid && !IsBusy);
        TestCommand = new AsyncRelayCommand(TestAsync, () => IsServerAddressValid && !IsBusy);
        FindCommand = new AsyncRelayCommand(FindAsync, () => !IsBusy);

        ServerAddress = _bootstrapOptions.ApiBaseUrl;
    }

    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand TestCommand { get; }
    public IAsyncRelayCommand FindCommand { get; }

    /// <summary>Handler the shell wires up to ask a yes/no question in the confirm overlay.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    [ObservableProperty] private string serverAddress = string.Empty;
    [ObservableProperty] private string status = string.Empty;
    [ObservableProperty] private bool isTesting;
    [ObservableProperty] private bool isFinding;

    /// <summary>The address this running copy is using; a saved change applies after the restart.</summary>
    public string CurrentServerAddress => _bootstrapOptions.ApiBaseUrl;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    private bool IsBusy => IsTesting || IsFinding;

    private bool IsServerAddressValid =>
        Uri.TryCreate(ServerAddress?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(uri.Host);

    partial void OnServerAddressChanged(string value) => NotifyCommands();
    partial void OnIsTestingChanged(bool value) => NotifyCommands();
    partial void OnIsFindingChanged(bool value) => NotifyCommands();
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        FindCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!IsServerAddressValid)
        {
            Status = $"Enter a valid server address, for example http://192.168.1.50:{ServerPort}.";
            return;
        }

        if (_hasUnsavedSettingsEdits() && !await ConfirmRestartAsync())
        {
            Status = "Not saved. Save or discard the settings edits first, then change the server address.";
            return;
        }

        var address = Normalize(ServerAddress);
        await _saveServerAddress(address, cancellationToken);
        Status = $"Saved {address}. Restarting Billing...";
        _restartApplication();
    }

    private async Task<bool> ConfirmRestartAsync()
        => ConfirmHandler is not null && await ConfirmHandler(new ConfirmRequest(
            "Restart Billing",
            "Billing must restart to use a different server. Unsaved Settings edits will be lost. Continue?",
            "Restart",
            IsWarning: true));

    private async Task TestAsync(CancellationToken cancellationToken)
    {
        IsTesting = true;
        Status = "Testing the server...";
        try
        {
            Status = await ProbeServerAsync(Normalize(ServerAddress), cancellationToken);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task FindAsync(CancellationToken cancellationToken)
    {
        IsFinding = true;
        Status = "Scanning the local network for a Tally Wrapper server...";
        try
        {
            var found = await FindServerCandidatesAsync(cancellationToken);
            if (found.Count == 0)
            {
                Status = "No Tally Wrapper server found on this subnet. Enter the server address by hand.";
                return;
            }

            ServerAddress = found[0];
            Status = found.Count == 1
                ? $"Found server: {found[0]}"
                : $"Found {found.Count} servers. Filled in {found[0]}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Server scan cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Server scan failed: {ex.Message}";
        }
        finally
        {
            IsFinding = false;
        }
    }

    private static string Normalize(string value)
    {
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : value.Trim();
    }

    private static async Task<string> ProbeServerAsync(string baseUrl, CancellationToken cancellationToken)
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
                return $"Something answered on {baseUrl}, but its health check returned HTTP {(int)live.StatusCode}.";
            }

            try
            {
                using var ready = await client.GetAsync("/api/health/ready", cancellationToken);
                if (ready.IsSuccessStatusCode)
                {
                    return $"Server reachable and its database is ready: {baseUrl}";
                }

                var body = await ready.Content.ReadAsStringAsync(cancellationToken);
                return $"Server reachable, but its database is not ready: HTTP {(int)ready.StatusCode}. {Summarize(body)}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return $"Server reachable, but its readiness check failed: {ex.Message}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"Cannot reach a server at {baseUrl}: {ex.Message}";
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
                var url = $"http://{address}:{ServerPort}";
                if (await LooksLikeServerAsync(url, cancellationToken))
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

    private static async Task<bool> LooksLikeServerAsync(string baseUrl, CancellationToken cancellationToken)
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

    /// <summary>Every other host on this PC's own /24 networks.</summary>
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

    private static string Summarize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "No response body.";
        }

        return body.Length <= 180 ? body : string.Concat(body.AsSpan(0, 180), "...");
    }
}
