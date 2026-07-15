using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels;

internal interface IShellHealthHost
{
    HealthClusterViewModel Health { get; }
    StatusBarViewModel StatusBar { get; }
    SystemState SystemState { get; set; }
    string BannerText { get; set; }
    SystemHealthSnapshot? LastHealthSnapshot { get; set; }
    DateTimeOffset? LastHealthCheckedAtUtc { get; set; }
    bool IsHealthRefreshing { get; set; }
    string? MastersRefreshMessage { get; set; }
    bool IsRefreshingAllMasters { get; set; }
    bool DatabaseConfigurationAttentionRequired { get; set; }
}

internal sealed class ShellHealthCoordinator(
    IHealthApiClient healthApiClient,
    IMastersApiClient mastersApiClient,
    IShellHealthHost host)
{
    private static readonly TimeSpan TallyHealthPollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DatabaseHealthPollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MasterFreshnessPollInterval = TimeSpan.FromMinutes(30);

    private int _healthPollInFlight;
    private DateTimeOffset? _lastDatabaseHealthCheckedAtUtc;
    private DateTimeOffset? _lastMasterFreshnessCheckedAtUtc;
    private DateTimeOffset? _lastTallyHealthCheckedAtUtc;

    public void ApplySystemState(SystemState value)
    {
        host.Health.Apply(value);
        host.BannerText = value switch
        {
            SystemState.Degraded => "Cloud reachable — Tally posting is manual. Click Push on a bill to send it to Tally.",
            SystemState.Limited => "Cloud unavailable — the desktop can prepare bills but cannot save. Retrying…",
            _ => string.Empty,
        };
    }

    public async Task RefreshAllMastersAsync()
    {
        if (host.IsRefreshingAllMasters) return;
        host.IsRefreshingAllMasters = true;
        host.MastersRefreshMessage = "Refreshing masters from Tally…";
        try
        {
            var results = await mastersApiClient.RequestRefreshAsync(
                new MasterRefreshRequest(null, "desktop-health-dialog"));

            var failed = results.Where(r => !r.Succeeded).ToList();
            if (failed.Count > 0)
            {
                var first = failed[0];
                var more = failed.Count > 1 ? $" (+{failed.Count - 1} more)" : string.Empty;
                host.MastersRefreshMessage = $"Refresh failed for {first.MasterType}: {first.ErrorMessage}{more}";
            }
            else
            {
                var counts = string.Join(", ", results.Select(r => $"{r.MasterType} ({r.ItemCount})"));
                host.MastersRefreshMessage = $"Refreshed · {counts}";
            }
        }
        catch (Exception ex)
        {
            host.MastersRefreshMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            host.IsRefreshingAllMasters = false;
        }
    }

    public async Task RefreshHealthAsync(
        bool forceTallyCompany = false,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _healthPollInFlight, 1) == 1) return;
        host.IsHealthRefreshing = true;
        try
        {
            SystemHealthSnapshot snapshot;
            var checkTallyCompany = ShouldCheckTallyCompany(forceTallyCompany);
            var checkDatabase = forceTallyCompany || ShouldCheckDatabaseHealth();
            var checkMasterFreshness = forceTallyCompany || ShouldCheckMasterFreshness();
            try
            {
                snapshot = await healthApiClient.GetSnapshotAsync(
                    checkTallyCompany,
                    checkMasterFreshness,
                    checkDatabase,
                    cancellationToken);
            }
            catch
            {
                snapshot = SystemHealthSnapshot.Unreachable();
            }

            if (snapshot.ApiReachable && !checkMasterFreshness && host.LastHealthSnapshot?.Masters is { } previousMasters)
            {
                snapshot = snapshot with { Masters = previousMasters };
            }
            else if (snapshot.Masters is not null)
            {
                _lastMasterFreshnessCheckedAtUtc = DateTimeOffset.UtcNow;
            }

            if (snapshot.ApiReachable && !checkTallyCompany && host.LastHealthSnapshot?.TallyCompany is { } previousTally)
            {
                snapshot = snapshot with { TallyCompany = previousTally };
            }
            else if (snapshot.TallyCompany is not null)
            {
                _lastTallyHealthCheckedAtUtc = DateTimeOffset.UtcNow;
            }

            if (snapshot.Runtime is { DatabaseHealthSkipped: false })
            {
                _lastDatabaseHealthCheckedAtUtc = DateTimeOffset.UtcNow;
            }

            host.LastHealthSnapshot = snapshot;
            host.LastHealthCheckedAtUtc = DateTimeOffset.UtcNow;
            ApplyHealthSnapshot(snapshot);
        }
        finally
        {
            host.IsHealthRefreshing = false;
            Interlocked.Exchange(ref _healthPollInFlight, 0);
        }
    }

    private void ApplyHealthSnapshot(SystemHealthSnapshot snapshot)
    {
        host.Health.Apply(snapshot);
        if (snapshot.Runtime is { DatabaseHealthSkipped: false } runtime)
        {
            host.StatusBar.ApplyDatabaseIdentity(runtime.DatabaseIdentity, null);
        }

        if (!snapshot.ApiReachable)
        {
            host.SystemState = SystemState.Limited;
            host.BannerText = "Cloud unavailable — the desktop can prepare bills but cannot save. Retrying…";
            host.StatusBar.StatusText = "CLOUD DOWN";
            return;
        }

        if (snapshot.Runtime?.DatabaseIdentityMatches == false)
        {
            host.SystemState = SystemState.Degraded;
            host.BannerText = snapshot.Runtime.Message;
            host.StatusBar.StatusText = "DB MISMATCH";
            return;
        }

        if (snapshot.Runtime?.DatabaseHealthSkipped == true)
        {
            host.SystemState = SystemState.Healthy;
            host.BannerText = string.Empty;
            host.StatusBar.StatusText = "READY";
            return;
        }

        if (snapshot.Runtime?.DatabaseReachable == false)
        {
            host.DatabaseConfigurationAttentionRequired = true;
            host.SystemState = SystemState.Limited;
            host.BannerText = "Database unavailable — open Settings > Advanced > Database to configure PostgreSQL.";
            host.StatusBar.StatusText = "DB DOWN";
            return;
        }

        host.SystemState = SystemState.Healthy;
        host.BannerText = string.Empty;
        host.StatusBar.StatusText = "READY";
    }

    private bool ShouldCheckTallyCompany(bool forceTallyCompany)
    {
        if (forceTallyCompany) return true;
        if (_lastTallyHealthCheckedAtUtc is null) return true;
        return DateTimeOffset.UtcNow - _lastTallyHealthCheckedAtUtc.Value >= TallyHealthPollInterval;
    }

    private bool ShouldCheckDatabaseHealth()
    {
        if (_lastDatabaseHealthCheckedAtUtc is null) return true;
        return DateTimeOffset.UtcNow - _lastDatabaseHealthCheckedAtUtc.Value >= DatabaseHealthPollInterval;
    }

    private bool ShouldCheckMasterFreshness()
    {
        if (_lastMasterFreshnessCheckedAtUtc is null) return true;
        return DateTimeOffset.UtcNow - _lastMasterFreshnessCheckedAtUtc.Value >= MasterFreshnessPollInterval;
    }
}
