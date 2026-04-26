using ShowroomBilling.Application.Tally;
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

    private sealed class FakeShellHealthHost : IShellHealthHost
    {
        public HealthClusterViewModel Health { get; } = new();
        public StatusBarViewModel StatusBar { get; } = new();
        public SystemState SystemState { get; set; }
        public string BannerText { get; set; } = string.Empty;
        public SystemHealthSnapshot? LastHealthSnapshot { get; set; }
        public DateTimeOffset? LastHealthCheckedAtUtc { get; set; }
        public bool IsHealthRefreshing { get; set; }
        public string? MastersRefreshMessage { get; set; }
        public bool IsRefreshingAllMasters { get; set; }
        public bool DatabaseConfigurationAttentionRequired { get; set; }
    }

    private sealed class FakeHealthApiClient(SystemHealthSnapshot snapshot) : IHealthApiClient
    {
        public Task<SystemHealthSnapshot> GetSnapshotAsync(
            bool includeTallyCompany,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FakeMastersApiClient : IMastersApiClient
    {
        public Task<CompanySnapshotResponse> GetCompaniesAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null) =>
            throw new NotSupportedException();

        public Task<LedgerSnapshotResponse> GetLedgersAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null) =>
            throw new NotSupportedException();

        public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null) =>
            throw new NotSupportedException();

        public Task<StockItemSnapshotResponse> GetStockItemsAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
            MasterRefreshRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
