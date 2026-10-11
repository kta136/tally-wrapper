using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class MasterSnapshotServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
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
                new CompanySnapshotItem("ACME Jewellers", true),
                new CompanySnapshotItem("Legacy Co", false)
            ]));

        Assert.Equal("companies", response.MasterType);
        Assert.Equal(2, response.ItemCount);

        var stored = await service.GetCompaniesAsync();
        Assert.Equal(2, stored.Companies.Count);
        Assert.Equal("fresh", stored.Metadata.Freshness);
        Assert.Equal(response.BatchId, stored.Metadata.BatchId);
        Assert.Contains(stored.Companies, c => c.Name == "ACME Jewellers" && c.IsActive);
    }

    [Fact]
    public async Task IngestCompaniesAsync_SupersedesPriorBatch()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow.AddMinutes(-5),
            [new CompanySnapshotItem("Old Co", true)]));

        var latest = await service.IngestCompaniesAsync(new PushCompanySnapshotRequest(
            ShowroomCode, DateTimeOffset.UtcNow,
            [new CompanySnapshotItem("ACME Jewellers", true)]));

        // A second context: the first still tracks the old batch as it was
        // loaded, and the service supersedes it with a set-based update.
        await using var stored = CreateDbContext();
        var batches = await stored.TallyMasterSnapshotBatches.ToListAsync();
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
                new LedgerSnapshotItem("Sales A/c", "  Sales Accounts  ", "Sales Accounts", true, null),
                new LedgerSnapshotItem("Cash", null, "Cash-in-Hand", false, null)
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
            [new StockItemSnapshotItem("22K Gold Ring", "22K-RING", "grams", "7113", "22K")]));

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
            [new VoucherTypeSnapshotItem("Sales", "Sales", true)]));

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
            [new CompanySnapshotItem("ACME", true)]));

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
}
