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
    // Every 15-second health poll has the server check its database. The Tally
    // company check talks to Tally and master freshness is for information
    // only, so those two are repeated only this often unless the operator asks.
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
    private bool _databaseAnsweredLastPoll;
    private DateTimeOffset? _lastMasterFreshnessCheckedAtUtc;
    private DateTimeOffset? _lastTallyHealthCheckedAtUtc;

    public void ApplySystemState(SystemState value)
    {
        host.Health.Apply(value);
        host.BannerText = value switch
        {
            SystemState.Degraded => "Tally posting is manual. Click Push on a bill to send it to Tally.",
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
            var checkMasterFreshness = forceTallyCompany || IsDue(_lastMasterFreshnessCheckedAtUtc);
            try
            {
                snapshot = await healthApiClient.GetSnapshotAsync(
                    checkTallyCompany,
                    checkMasterFreshness,
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

            host.LastHealthSnapshot = snapshot;
            host.LastHealthCheckedAtUtc = DateTimeOffset.UtcNow;
            ApplyHealthSnapshot(snapshot, operatorAsked: forceTallyCompany);
        }
        finally
        {
            Interlocked.Exchange(ref _healthPollInFlight, 0);
        }
    }

    private void ApplyHealthSnapshot(SystemHealthSnapshot snapshot, bool operatorAsked)
    {
        // The database is across the internet from the server, so one poll it
        // did not answer is not yet an outage. The database pill turns red
        // straight away; the bill on screen is taken away only when the next
        // poll agrees, or when the operator asked for this check.
        var databaseDown = snapshot.Runtime?.DatabaseReachable == false;
        var firstMiss = databaseDown && _databaseAnsweredLastPoll && !operatorAsked;
        _databaseAnsweredLastPoll = snapshot.Runtime?.DatabaseReachable == true;

        host.Health.Apply(snapshot);

        if (!snapshot.ApiReachable)
        {
            host.SystemState = SystemState.Limited;
            host.BannerText = CloudUnavailableBanner;
            host.StatusBar.StatusText = "CLOUD DOWN";
            return;
        }

        if (firstMiss)
        {
            return;
        }

        if (snapshot.Runtime is { } runtime)
        {
            host.StatusBar.ApplyDatabaseIdentity(runtime.DatabaseIdentity);
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

        if (databaseDown)
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
