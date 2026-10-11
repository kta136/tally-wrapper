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
            ApiAvailable: true,
            DatabaseConfigured: true,
            DatabaseReachable: true,
            SettingsLoadedFromApi: true,
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
    public async Task RefreshHealthAsync_SaysTheDatabaseNeedsItsMigrations_AndKeepsAskingUntilItHasThem()
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

        // The next poll would normally be the cheap one (the database was
        // checked a moment ago). While it is behind, the full check is asked for.
        client.Snapshot = new SystemHealthSnapshot(true, null, null, Runtime(pending: 0));
        await coordinator.RefreshHealthAsync();

        Assert.True(client.LastForceDatabaseHealth);
        Assert.Equal(SystemState.Healthy, host.SystemState);
        Assert.Equal("READY", host.StatusBar.StatusText);
        Assert.Equal(string.Empty, host.BannerText);

        // Up to date again: back to the cheap probe.
        await coordinator.RefreshHealthAsync();
        Assert.False(client.LastForceDatabaseHealth);
    }

    private static RuntimeHealthResponse Runtime(int pending) => new(
        pending > 0 ? "Unhealthy" : "Healthy",
        ApiAvailable: true,
        DatabaseConfigured: true,
        DatabaseReachable: true,
        SettingsLoadedFromApi: true,
        Message: "from the server",
        DatabaseIdentity: "PROD",
        ExpectedDatabaseIdentity: "PROD",
        DatabaseIdentityMatches: true,
        PendingMigrationCount: pending);

    private sealed class FakeShellHealthHost : IShellHealthHost
    {
        public HealthClusterViewModel Health { get; } = new();
        public StatusBarViewModel StatusBar { get; } = new();
        public SystemState SystemState { get; set; }
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

        public bool LastForceDatabaseHealth { get; private set; }

        public Task<SystemHealthSnapshot> GetSnapshotAsync(
            bool includeTallyCompany,
            bool includeMasterFreshness = true,
            bool forceDatabaseHealth = false,
            CancellationToken cancellationToken = default)
        {
            LastForceDatabaseHealth = forceDatabaseHealth;
            return Task.FromResult(Snapshot);
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

        public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
            MasterRefreshRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
