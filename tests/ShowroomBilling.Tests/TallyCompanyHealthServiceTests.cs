using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Tally;
using ShowroomBilling.Tests.TestSupport;

namespace ShowroomBilling.Tests;

public sealed class TallyCompanyHealthServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsHealthy_WhenActiveCompanyIsOpen()
    {
        var service = BuildService(
            activeCompany: "Acme Jewellers",
            companies: [new CompanySnapshotItem("  acme jewellers  ", true)]);

        var response = await service.CheckAsync();

        Assert.Equal("healthy", response.Status);
        Assert.True(response.TallyReachable);
        Assert.True(response.ActiveCompanyOpen);
        Assert.Equal("Acme Jewellers", response.ActiveCompanyName);
        Assert.Equal(1, response.CompanyCount);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenTallyAnswersButActiveCompanyIsMissing()
    {
        var service = BuildService(
            activeCompany: "Acme Jewellers",
            companies: [new CompanySnapshotItem("Other Company", true)]);

        var response = await service.CheckAsync();

        Assert.Equal("warning", response.Status);
        Assert.True(response.TallyReachable);
        Assert.False(response.ActiveCompanyOpen);
        Assert.Equal(1, response.CompanyCount);
        Assert.Contains("not open", response.Message);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarning_WhenActiveCompanyIsBlank()
    {
        var service = BuildService(activeCompany: " ", companies: []);

        var response = await service.CheckAsync();

        Assert.Equal("warning", response.Status);
        Assert.False(response.TallyReachable);
        Assert.False(response.ActiveCompanyOpen);
        Assert.Contains("not configured", response.Message);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnhealthy_WhenSettingsAreUnavailable()
    {
        var service = new TallyCompanyHealthService(
            new FakeCloudSettingsService { ReadFailure = new InvalidOperationException("db down") },
            new FakeTallyMasterSource([]),
            NullLogger<TallyCompanyHealthService>.Instance);

        var response = await service.CheckAsync();

        Assert.Equal("unhealthy", response.Status);
        Assert.False(response.TallyReachable);
        Assert.Contains("settings", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnhealthy_WhenTallyCompanyFetchReturnsNull()
    {
        var service = BuildService(activeCompany: "Acme Jewellers", companies: null);

        var response = await service.CheckAsync();

        Assert.Equal("unhealthy", response.Status);
        Assert.False(response.TallyReachable);
        Assert.False(response.ActiveCompanyOpen);
        Assert.Contains("unreachable", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TallyCompanyHealthService BuildService(
        string activeCompany,
        IReadOnlyList<CompanySnapshotItem>? companies)
    {
        return new TallyCompanyHealthService(
            new FakeCloudSettingsService { ActiveCompany = activeCompany },
            new FakeTallyMasterSource(companies),
            NullLogger<TallyCompanyHealthService>.Instance);
    }


    private sealed class FakeTallyMasterSource(IReadOnlyList<CompanySnapshotItem>? companies) : ITallyMasterSource
    {
        public Task<IReadOnlyList<CompanySnapshotItem>> FetchCompaniesAsync(CancellationToken cancellationToken = default) =>
            companies is null
                ? Task.FromException<IReadOnlyList<CompanySnapshotItem>>(new HttpRequestException("Connection refused"))
                : Task.FromResult(companies);

        public Task<IReadOnlyList<LedgerSnapshotItem>> FetchLedgersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StockItemSnapshotItem>> FetchStockItemsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VoucherTypeSnapshotItem>> FetchVoucherTypesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
