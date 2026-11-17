using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Tests;

public sealed class ShellHealthCoordinatorTests
{
    [Fact]
    public async Task RefreshHealthAsync_MapsDatabaseIdentityMismatchToWarning()
    {
        var host = new FakeShellHealthHost();
        var runtime = new RuntimeHealthResponse(
            "Healthy",
            DatabaseReachable: true,
            Message: "Database identity mismatch: PostgreSQL is reachable, but database identity is PROD; expected DEV for Development.",
            DatabaseIdentity: "PROD",
            ExpectedDatabaseIdentity: "DEV",
            DatabaseIdentityMatches: false);
        var coordinator = new ShellHealthCoordinator(
            new FakeHealthApiClient(new SystemHealthSnapshot(true, null, null, runtime)),
            new FakeMastersApiClient(),
            host);

        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Degraded, host.SystemState);
        Assert.Equal("DB MISMATCH", host.StatusBar.StatusText);
        Assert.Equal("DB PROD", host.StatusBar.DatabaseEnvironment);
        Assert.Equal("warn", host.Health.DatabaseDot);
        Assert.Equal("DB PROD", host.Health.DatabaseLabel);
        Assert.Contains("Database identity mismatch", host.BannerText);
        Assert.False(host.DatabaseConfigurationAttentionRequired);
    }

    [Fact]
    public async Task RefreshHealthAsync_SaysTheDatabaseNeedsItsMigrations_UntilItHasThem()
    {
        var host = new FakeShellHealthHost();
        var client = new FakeHealthApiClient(new SystemHealthSnapshot(true, null, null, Runtime(pending: 6)));
        var coordinator = new ShellHealthCoordinator(client, new FakeMastersApiClient(), host);

        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Limited, host.SystemState);
        Assert.Equal("DB BEHIND", host.StatusBar.StatusText);
        Assert.Equal("err", host.Health.DatabaseDot);
        Assert.Contains("6 schema migration(s)", host.BannerText);
        Assert.Contains("administrator", host.BannerText);

        // Every poll is a real check, so the banner goes with the first one
        // after the migrations have been applied.
        client.Snapshot = new SystemHealthSnapshot(true, null, null, Runtime(pending: 0));
        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Healthy, host.SystemState);
        Assert.Equal("READY", host.StatusBar.StatusText);
        Assert.Equal(string.Empty, host.BannerText);
    }

    [Fact]
    public async Task RefreshAllMastersAsync_ReadsTheListsAgain_SoTheHealthDialogShowsThemRefreshed()
    {
        var host = new FakeShellHealthHost();
        var never = new MasterFreshnessSummaryResponse("missing", [new MasterSnapshotMetadata("ledgers", null, 0, "missing")]);
        var fresh = new MasterFreshnessSummaryResponse("fresh", [new MasterSnapshotMetadata("ledgers", DateTimeOffset.UtcNow, 9, "fresh")]);
        var client = new FakeHealthApiClient(new SystemHealthSnapshot(true, never, null, Runtime(pending: 0)));
        var masters = new FakeMastersApiClient
        {
            Refreshed = () => client.Snapshot = new SystemHealthSnapshot(true, fresh, null, Runtime(pending: 0)),
        };
        var coordinator = new ShellHealthCoordinator(client, masters, host);
        await coordinator.RefreshHealthAsync();
        Assert.Same(never, host.LastHealthSnapshot?.Masters);

        await coordinator.RefreshAllMastersAsync();

        Assert.True(client.LastPollAskedForMasters);
        Assert.Same(fresh, host.LastHealthSnapshot?.Masters);
        Assert.False(host.IsRefreshingAllMasters);
        Assert.Contains("ledgers", host.MastersRefreshMessage);
    }

    [Fact]
    public async Task RefreshHealthAsync_AskedForWhileAPollIsOut_GetsItsOwnAnswerAndIsNotDropped()
    {
        var host = new FakeShellHealthHost();
        var client = new FakeHealthApiClient(new SystemHealthSnapshot(true, null, null, Runtime(pending: 0)));
        var coordinator = new ShellHealthCoordinator(client, new FakeMastersApiClient(), host);
        await coordinator.RefreshHealthAsync();

        // The timer's poll is out and has not come back.
        var hold = new TaskCompletionSource();
        client.HoldNextPoll = hold;
        var timerPoll = coordinator.RefreshHealthAsync();

        // The operator presses Refresh meanwhile; another timer tick adds nothing.
        var asked = coordinator.RefreshHealthAsync(forceTallyCompany: true);
        await coordinator.RefreshHealthAsync();
        Assert.False(asked.IsCompleted);
        Assert.Equal(2, client.Polls);

        client.Snapshot = new SystemHealthSnapshot(true, null, null, DatabaseDown());
        hold.SetResult();
        await Task.WhenAll(timerPoll, asked);

        // The timer's answer alone would have been one missed answer, which
        // leaves the screen as it was. The operator's own check came after it
        // and is acted on at once.
        Assert.Equal(3, client.Polls);
        Assert.Equal(SystemState.Limited, host.SystemState);
        Assert.Equal("DB DOWN", host.StatusBar.StatusText);
    }

    [Fact]
    public async Task RefreshHealthAsync_KeepsTheBillOnScreenThroughOneMissedAnswerFromTheDatabase()
    {
        var host = new FakeShellHealthHost();
        var client = new FakeHealthApiClient(new SystemHealthSnapshot(true, null, null, Runtime(pending: 0)));
        var coordinator = new ShellHealthCoordinator(client, new FakeMastersApiClient(), host);
        await coordinator.RefreshHealthAsync();
        Assert.Equal(SystemState.Healthy, host.SystemState);

        // One poll without an answer: the pill says so, the screen stays.
        client.Snapshot = new SystemHealthSnapshot(true, null, null, DatabaseDown());
        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Healthy, host.SystemState);
        Assert.Equal("READY", host.StatusBar.StatusText);
        Assert.Equal("DB PROD", host.StatusBar.DatabaseEnvironment);
        Assert.Equal("err", host.Health.DatabaseDot);

        // The next poll agrees: it is an outage.
        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Limited, host.SystemState);
        Assert.Equal("DB DOWN", host.StatusBar.StatusText);
        Assert.Contains("cannot reach its database", host.BannerText);

        // It is over with the first poll that gets an answer.
        client.Snapshot = new SystemHealthSnapshot(true, null, null, Runtime(pending: 0));
        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Healthy, host.SystemState);
        Assert.Equal("READY", host.StatusBar.StatusText);
        Assert.Equal(string.Empty, host.BannerText);
    }

    [Fact]
    public async Task RefreshHealthAsync_ReportsADatabaseThatIsDownAtOnce_AtStartUpAndWhenTheOperatorAsks()
    {
        // At start-up there is no earlier answer to stay with.
        var host = new FakeShellHealthHost();
        var client = new FakeHealthApiClient(new SystemHealthSnapshot(true, null, null, DatabaseDown()));
        var coordinator = new ShellHealthCoordinator(client, new FakeMastersApiClient(), host);
        await coordinator.RefreshHealthAsync();

        Assert.Equal(SystemState.Limited, host.SystemState);
        Assert.Equal("DB DOWN", host.StatusBar.StatusText);

        client.Snapshot = new SystemHealthSnapshot(true, null, null, Runtime(pending: 0));
        await coordinator.RefreshHealthAsync();
        Assert.Equal("READY", host.StatusBar.StatusText);

        // Refresh in System Health shows the check as it came back.
        client.Snapshot = new SystemHealthSnapshot(true, null, null, DatabaseDown());
        await coordinator.RefreshHealthAsync(forceTallyCompany: true);

        Assert.Equal(SystemState.Limited, host.SystemState);
        Assert.Equal("DB DOWN", host.StatusBar.StatusText);
    }

    /// <summary>
    /// The shell writes general words on the pills whenever its state changes ("Cloud unreachable"
    /// for every limited state). When the state changed because of what the server just answered,
    /// that answer is what the pills must say: with only the database down, the server is reachable
    /// and Tally is as it was found.
    /// </summary>
    [Fact]
    public async Task RefreshHealthAsync_WithOnlyTheDatabaseDown_TheCloudAndTallyPillsSayWhatWasFound()
    {
        var host = new FakeShellHealthHost();
        var tally = new TallyCompanyHealthResponse(
            TallyHealthStatuses.Healthy, true, "Company A", true, 1, DateTimeOffset.UtcNow, "Tally OK - active company 'Company A' is open.");
        var client = new FakeHealthApiClient(new SystemHealthSnapshot(true, null, tally, Runtime(pending: 0)));
        var coordinator = new ShellHealthCoordinator(client, new FakeMastersApiClient(), host);
        // As MainWindowViewModel does on every change of its state.
        host.StateChanged = coordinator.ApplySystemState;
        host.SystemState = SystemState.Degraded;

        await coordinator.RefreshHealthAsync(forceTallyCompany: true);

        Assert.Equal(SystemState.Healthy, host.SystemState);
        Assert.Equal("Cloud reachable", host.Health.CloudTooltip);
        Assert.Equal("ok", host.Health.TallyDot);
        Assert.Contains("Tally OK", host.Health.TallyTooltip);

        client.Snapshot = new SystemHealthSnapshot(true, null, tally, DatabaseDown());
        await coordinator.RefreshHealthAsync(forceTallyCompany: true);

        Assert.Equal(SystemState.Limited, host.SystemState);
        Assert.Contains("cannot reach its database", host.BannerText);
        Assert.Equal("err", host.Health.DatabaseDot);
        Assert.Equal("ok", host.Health.CloudDot);
        Assert.Equal("Cloud reachable", host.Health.CloudTooltip);
        Assert.Equal("ok", host.Health.TallyDot);
        Assert.Contains("Tally OK", host.Health.TallyTooltip);

        // A server that cannot be reached at all is still said to be so.
        client.Snapshot = SystemHealthSnapshot.Unreachable();
        await coordinator.RefreshHealthAsync(forceTallyCompany: true);

        Assert.Equal("err", host.Health.CloudDot);
        Assert.Equal("Cloud unreachable — API offline", host.Health.CloudTooltip);
    }

    private static RuntimeHealthResponse DatabaseDown() => new(
        "Unhealthy",
        DatabaseReachable: false,
        Message: "API is online. PostgreSQL must be configured and reachable for readiness.",
        ExpectedDatabaseIdentity: "PROD");

    private static RuntimeHealthResponse Runtime(int pending) => new(
        pending > 0 ? "Unhealthy" : "Healthy",
        DatabaseReachable: true,
        Message: "from the server",
        DatabaseIdentity: "PROD",
        ExpectedDatabaseIdentity: "PROD",
        DatabaseIdentityMatches: true,
        PendingMigrationCount: pending);

    private sealed class FakeShellHealthHost : IShellHealthHost
    {
        public HealthClusterViewModel Health { get; } = new();
        public StatusBarViewModel StatusBar { get; } = new();
        private SystemState _systemState;

        /// <summary>What the shell runs when its state changes; nothing unless a test sets it.</summary>
        public Action<SystemState>? StateChanged { get; set; }

        public SystemState SystemState
        {
            get => _systemState;
            set
            {
                if (_systemState == value)
                {
                    return;
                }

                _systemState = value;
                StateChanged?.Invoke(value);
            }
        }

        public string BannerText { get; set; } = string.Empty;
        public SystemHealthSnapshot? LastHealthSnapshot { get; set; }
        public DateTimeOffset? LastHealthCheckedAtUtc { get; set; }
        public string? MastersRefreshMessage { get; set; }
        public bool IsRefreshingAllMasters { get; set; }
        public bool DatabaseConfigurationAttentionRequired { get; set; }
    }

    private sealed class FakeHealthApiClient(SystemHealthSnapshot snapshot) : IHealthApiClient
    {
        public SystemHealthSnapshot Snapshot { get; set; } = snapshot;

        /// <summary>How many polls reached the server.</summary>
        public int Polls { get; private set; }

        public bool LastPollAskedForMasters { get; private set; }

        /// <summary>When set, the next poll does not come back until it is completed.</summary>
        public TaskCompletionSource? HoldNextPoll { get; set; }

        public async Task<SystemHealthSnapshot> GetSnapshotAsync(
            bool includeTallyCompany,
            bool includeMasterFreshness = true,
            CancellationToken cancellationToken = default)
        {
            Polls++;
            LastPollAskedForMasters = includeMasterFreshness;
            if (HoldNextPoll is { } hold)
            {
                HoldNextPoll = null;
                await hold.Task;
            }

            return Snapshot;
        }
    }

    private sealed class FakeMastersApiClient : IMastersApiClient
    {
        public Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>What the server makes of the lists once they have been refreshed, for the health poll that follows.</summary>
        public Action? Refreshed { get; set; }

        public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
            MasterRefreshRequest request,
            CancellationToken cancellationToken = default)
        {
            Refreshed?.Invoke();
            return Task.FromResult<IReadOnlyList<TallyMasterRefreshResult>>(
                [new TallyMasterRefreshResult("ledgers", Succeeded: true, ItemCount: 9, ErrorMessage: null)]);
        }
    }
}
