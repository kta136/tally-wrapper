using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels;

internal interface IShellHealthHost
{
    HealthClusterViewModel Health { get; }
    StatusBarViewModel StatusBar { get; }
    SystemState SystemState { get; set; }
    string BannerText { get; set; }
    SystemHealthSnapshot? LastHealthSnapshot { get; set; }
    DateTimeOffset? LastHealthCheckedAtUtc { get; set; }
    string? MastersRefreshMessage { get; set; }
    bool IsRefreshingAllMasters { get; set; }
}

internal sealed class ShellHealthCoordinator(
    IHealthApiClient healthApiClient,
    IMastersApiClient mastersApiClient,
    IShellHealthHost host)
{
    // The 15-second health poll only asks whether the API answers. The Tally
    // company, database and master-freshness checks cost a round trip each, so
    // each is repeated only this often unless the operator asks.
    private static readonly TimeSpan SlowCheckInterval = TimeSpan.FromMinutes(30);

    private const string CloudUnavailableBanner =
        "Cloud unavailable — the desktop can prepare bills but cannot save. Retrying…";

    /// <summary>
    /// The server is up and the database answers, but it has not had the
    /// migrations this version needs, so no bill can be read or saved.
    /// </summary>
    internal static string SchemaBehindBanner(int pendingMigrations) =>
        $"Database upgrade needed — this version expects {pendingMigrations} schema migration(s) the database has not had. "
        + "Bills cannot be loaded or saved until the administrator applies them.";

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
            SystemState.Limited => CloudUnavailableBanner,
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

            host.MastersRefreshMessage = SettingsRefreshSummaryFormatter.Summarize(results);
        }
        catch (Exception ex)
        {
            host.MastersRefreshMessage = $"Refresh failed: {ApiResponseReader.FormatError(ex)}";
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
        try
        {
            SystemHealthSnapshot snapshot;
            var checkTallyCompany = forceTallyCompany || IsDue(_lastTallyHealthCheckedAtUtc);
            // While the database is behind, ask it again on every poll, so
            // the banner goes within seconds of the migrations being applied.
            var checkDatabase = forceTallyCompany
                || IsDue(_lastDatabaseHealthCheckedAtUtc)
                || host.LastHealthSnapshot?.Runtime?.PendingMigrationCount > 0;
            var checkMasterFreshness = forceTallyCompany || IsDue(_lastMasterFreshnessCheckedAtUtc);
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
            Interlocked.Exchange(ref _healthPollInFlight, 0);
        }
    }

    private void ApplyHealthSnapshot(SystemHealthSnapshot snapshot)
    {
        host.Health.Apply(snapshot);
        if (snapshot.Runtime is { DatabaseHealthSkipped: false } runtime)
        {
            host.StatusBar.ApplyDatabaseIdentity(runtime.DatabaseIdentity);
        }

        if (!snapshot.ApiReachable)
        {
            host.SystemState = SystemState.Limited;
            host.BannerText = CloudUnavailableBanner;
            host.StatusBar.StatusText = "CLOUD DOWN";
            return;
        }

        if (snapshot.Runtime?.PendingMigrationCount > 0)
        {
            host.SystemState = SystemState.Limited;
            host.BannerText = SchemaBehindBanner(snapshot.Runtime.PendingMigrationCount.Value);
            host.StatusBar.StatusText = "DB BEHIND";
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
            host.SystemState = SystemState.Limited;
            host.BannerText = "The server cannot reach its database. On the server PC, open the Tally Wrapper tray and check the database connection.";
            host.StatusBar.StatusText = "DB DOWN";
            return;
        }

        host.SystemState = SystemState.Healthy;
        host.BannerText = string.Empty;
        host.StatusBar.StatusText = "READY";
    }

    private static bool IsDue(DateTimeOffset? lastCheckedAtUtc)
        => lastCheckedAtUtc is null || DateTimeOffset.UtcNow - lastCheckedAtUtc.Value >= SlowCheckInterval;
}
