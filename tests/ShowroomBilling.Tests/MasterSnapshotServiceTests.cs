using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class MasterSnapshotServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task IngestCompaniesAsync_StoresTheListAsOneRow()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        var stored = await service.IngestCompaniesAsync(
        [
            new CompanySnapshotItem("Legacy Co", false),
            new CompanySnapshotItem("ACME Jewellers", true),
        ]);

        Assert.Equal("companies", stored.MasterType);
        Assert.Equal(2, stored.ItemCount);
        Assert.Equal("fresh", stored.Freshness);

        var read = await service.GetCompaniesAsync();
        Assert.Equal(["ACME Jewellers", "Legacy Co"], read.Companies.Select(c => c.Name));
        Assert.True(read.Companies[0].IsActive);
        Assert.Equal("fresh", read.Metadata.Freshness);
        Assert.Equal(stored.FetchedAtUtc, read.Metadata.FetchedAtUtc);

        await using var second = CreateDbContext();
        var row = Assert.Single(await second.TallyMasters.AsNoTracking().ToListAsync());
        Assert.Equal("companies", row.MasterType);
        Assert.Equal(2, row.ItemCount);
        Assert.Equal(2, JsonDocument.Parse(row.ItemsJson).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_second_ingest_replaces_the_list()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestCompaniesAsync([new CompanySnapshotItem("Old Co", true), new CompanySnapshotItem("Older Co", true)]);
        await service.IngestCompaniesAsync([new CompanySnapshotItem("ACME Jewellers", true)]);

        await using var second = CreateDbContext();
        var row = Assert.Single(await second.TallyMasters.AsNoTracking().ToListAsync());
        Assert.Equal(1, row.ItemCount);

        var read = await service.GetCompaniesAsync();
        Assert.Equal("ACME Jewellers", Assert.Single(read.Companies).Name);

        // Each ingest leaves its line in the audit trail.
        Assert.Equal(2, await second.AuditEvents.CountAsync(e => e.EventType == "masters.companies.ingested"));
    }

    /// <summary>The first refresh of a master on two counters at once must not fail one of them.</summary>
    [Fact]
    public async Task Two_first_ingests_at_once_both_succeed()
    {
        await using var first = CreateDbContext();
        await using var second = CreateDbContext();

        await Task.WhenAll(
            new MasterSnapshotService(first).IngestLedgersAsync([new LedgerSnapshotItem("Cash")]),
            new MasterSnapshotService(second).IngestLedgersAsync([new LedgerSnapshotItem("Cash")]));

        await using var check = CreateDbContext();
        Assert.Single(await check.TallyMasters.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task IngestLedgersAsync_TrimsNames()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        var stored = await service.IngestLedgersAsync(
        [
            new LedgerSnapshotItem("  Sales A/c  "),
            new LedgerSnapshotItem("Cash"),
        ]);

        Assert.Equal(2, stored.ItemCount);

        var read = await service.GetLedgersAsync();
        Assert.Equal(["Cash", "Sales A/c"], read.Ledgers.Select(l => l.Name));
    }

    /// <summary>
    /// The read used to stop at 500 rows while reporting the full count, so a
    /// company with more ledgers lost the rest from the Settings dropdown.
    /// </summary>
    [Fact]
    public async Task GetLedgersAsync_ReturnsEveryLedger_HoweverMany()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);
        var ledgers = Enumerable.Range(1, 501)
            .Reverse()
            .Select(n => new LedgerSnapshotItem($"Customer {n:0000}"))
            .ToArray();
        await service.IngestLedgersAsync(ledgers);

        var read = await service.GetLedgersAsync();

        Assert.Equal(501, read.Ledgers.Count);
        Assert.Equal(501, read.Metadata.ItemCount);
        Assert.Equal("Customer 0001", read.Ledgers[0].Name);
        Assert.Equal("Customer 0501", read.Ledgers[^1].Name);
    }

    [Fact]
    public async Task IngestStockItemsAsync_KeepsUnitAndHsn()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestStockItemsAsync(
        [
            new StockItemSnapshotItem("22K Gold Ring", " grams ", "7113"),
            new StockItemSnapshotItem("Loose Stone", "  ", null),
        ]);

        var read = await service.GetStockItemsAsync();
        Assert.Equal(2, read.StockItems.Count);
        Assert.Equal(new StockItemSnapshotItem("22K Gold Ring", "grams", "7113"), read.StockItems[0]);
        Assert.Equal(new StockItemSnapshotItem("Loose Stone", null, null), read.StockItems[1]);
    }

    [Fact]
    public async Task IngestVoucherTypesAsync_PersistsItems()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestVoucherTypesAsync([new VoucherTypeSnapshotItem("Sales")]);

        var read = await service.GetVoucherTypesAsync();
        Assert.Equal("Sales", Assert.Single(read.VoucherTypes).Name);
        Assert.Equal("voucher_types", read.Metadata.MasterType);
    }

    [Fact]
    public async Task GetFreshnessSummaryAsync_ReportsPerTypeStatus()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        await service.IngestCompaniesAsync([new CompanySnapshotItem("ACME", true)]);

        var summary = await service.GetFreshnessSummaryAsync();

        Assert.Equal(4, summary.Masters.Count);
        Assert.Contains(summary.Masters, m => m.MasterType == "companies" && m.Freshness == "fresh" && m.ItemCount == 1);
        Assert.Contains(summary.Masters, m => m.MasterType == "ledgers" && m.Freshness == "missing");
        Assert.Equal("missing", summary.OverallStatus);
    }

    [Fact]
    public async Task A_list_read_long_ago_is_reported_stale()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);
        await service.IngestCompaniesAsync([new CompanySnapshotItem("ACME", true)]);

        await using (var second = CreateDbContext())
        {
            await second.TallyMasters.ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.FetchedAtUtc, DateTimeOffset.UtcNow.AddHours(-7)));
        }

        Assert.Equal("stale", (await service.GetCompaniesAsync()).Metadata.Freshness);
    }

    [Fact]
    public async Task GetCompaniesAsync_EmptyWhenNoIngestion()
    {
        await using var db = CreateDbContext();
        var service = new MasterSnapshotService(db);

        var read = await service.GetCompaniesAsync();
        Assert.Empty(read.Companies);
        Assert.Equal("missing", read.Metadata.Freshness);
        Assert.Null(read.Metadata.FetchedAtUtc);
    }
}
