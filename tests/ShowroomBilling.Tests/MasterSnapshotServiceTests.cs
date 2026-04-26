using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests;

public sealed class MasterSnapshotServiceTests
{
    private const string ShowroomCode = "DEV-SHOWROOM";

    [Fact]
    public async Task IngestCompaniesAsync_PersistsBatchAndItems()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        var response = await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [
                new CompanySnapshotItem("ACME Jewellers", true, null),
                new CompanySnapshotItem("Legacy Co", false, "{\"tag\":\"legacy\"}")
            ]));

        Assert.Equal("companies", response.MasterType);
        Assert.Equal(2, response.ItemCount);

        var stored = await service.GetCompaniesAsync();
        Assert.Equal(2, stored.Companies.Count);
        Assert.Equal("fresh", stored.Metadata.Freshness);
        Assert.Equal(response.BatchId, stored.Metadata.BatchId);
        Assert.Contains(stored.Companies, c => c.Name == "ACME Jewellers" && c.IsActive);
        Assert.All(stored.Companies, c => Assert.Null(c.RawJson));
    }

    [Fact]
    public async Task GetCompaniesAsync_ReturnsRawJsonOnlyWhenRequested()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [new CompanySnapshotItem("Raw Co", true, "{\"tag\":\"raw\"}")]));

        var defaultRead = await service.GetCompaniesAsync();
        var rawRead = await service.GetCompaniesAsync(query: new MasterSnapshotQuery(null, null, null, IncludeRaw: true));

        Assert.Null(defaultRead.Companies.Single().RawJson);
        Assert.Equal("{\"tag\":\"raw\"}", rawRead.Companies.Single().RawJson);
    }

    [Fact]
    public async Task IngestCompaniesAsync_SupersedesPriorBatch()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow.AddMinutes(-5),
            [new CompanySnapshotItem("Old Co", true, null)]));

        var latest = await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [new CompanySnapshotItem("ACME Jewellers", true, null)]));

        var batches = await db.TallyMasterSnapshotBatches.ToListAsync();
        Assert.Equal(2, batches.Count);
        Assert.Single(batches, b => b.Status == "active" && b.Id.ToString() == latest.BatchId);
        Assert.Single(batches, b => b.Status == "superseded");

        var read = await service.GetCompaniesAsync();
        Assert.Single(read.Companies);
        Assert.Equal("ACME Jewellers", read.Companies[0].Name);
    }

    [Fact]
    public async Task IngestLedgersAsync_PersistsNormalizedRows()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        var response = await service.IngestLedgersAsync(new PushLedgerSnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [
                new LedgerSnapshotItem("Sales A/c", "  Sales Accounts  ", "Sales Accounts", true, null, null),
                new LedgerSnapshotItem("Cash", null, "Cash-in-Hand", false, null, null)
            ]));

        Assert.Equal(2, response.ItemCount);

        var read = await service.GetLedgersAsync();
        Assert.Equal(2, read.Ledgers.Count);
        Assert.Contains(read.Ledgers, l => l.Name == "Sales A/c" && l.Parent == "Sales Accounts" && l.IsRevenue);
    }

    [Fact]
    public async Task IngestStockItemsAsync_PersistsKaratAndHsn()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestStockItemsAsync(new PushStockItemSnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [new StockItemSnapshotItem("22K Gold Ring", "22K-RING", "grams", "7113", "22K", null)]));

        var read = await service.GetStockItemsAsync();
        Assert.Single(read.StockItems);
        Assert.Equal("22K", read.StockItems[0].Karat);
        Assert.Equal("7113", read.StockItems[0].HsnCode);
    }

    [Fact]
    public async Task IngestVoucherTypesAsync_PersistsItems()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestVoucherTypesAsync(new PushVoucherTypeSnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [new VoucherTypeSnapshotItem("Sales", "Sales", true, null)]));

        var read = await service.GetVoucherTypesAsync();
        Assert.Single(read.VoucherTypes);
        Assert.Equal("Sales", read.VoucherTypes[0].Name);
        Assert.True(read.VoucherTypes[0].IsDeemedPositive);
    }

    [Fact]
    public async Task GetFreshnessSummaryAsync_ReportsPerTypeStatus()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [new CompanySnapshotItem("ACME", true, null)]));

        var summary = await service.GetFreshnessSummaryAsync();

        Assert.Equal(4, summary.Masters.Count);
        Assert.Contains(summary.Masters, m => m.MasterType == "companies" && m.Freshness == "fresh");
        Assert.Contains(summary.Masters, m => m.MasterType == "ledgers" && m.Freshness == "missing");
        Assert.Equal("missing", summary.OverallStatus);
    }

    [Fact]
    public async Task GetCompaniesAsync_EmptyWhenNoIngestion()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        var read = await service.GetCompaniesAsync();
        Assert.Empty(read.Companies);
        Assert.Equal("missing", read.Metadata.Freshness);
        Assert.Null(read.Metadata.BatchId);
    }

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }
}
