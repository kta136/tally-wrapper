using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class NumberingServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task Preview_WithNoSequence_ReturnsOneAndDoesNotReserve()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var preview1 = await service.GetPreviewAsync("2026-27", CancellationToken.None);
        var preview2 = await service.GetPreviewAsync("2026-27", CancellationToken.None);

        Assert.Equal(1L, preview1.PreviewValue);
        Assert.Equal(1L, preview2.PreviewValue);
        Assert.Empty(await db.InvoiceSequences.ToListAsync());
    }

    [Fact]
    public async Task Reserve_AllocatesSequentialValues()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var first = await ReserveAsync(db, service, "2026-27");
        var second = await ReserveAsync(db, service, "2026-27");
        var third = await ReserveAsync(db, service, "2026-27");

        Assert.Equal(1L, first.ReservedValue);
        Assert.Equal(2L, second.ReservedValue);
        Assert.Equal(3L, third.ReservedValue);
        Assert.Contains("2026-27/0003", third.FormattedNumber);
    }

    [Fact]
    public async Task Reserve_AdvancesSequence_SoPreviewReflectsNext()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await ReserveAsync(db, service, "2026-27");
        await ReserveAsync(db, service, "2026-27");

        var preview = await service.GetPreviewAsync("2026-27", CancellationToken.None);

        Assert.Equal(3L, preview.PreviewValue);
    }

    [Fact]
    public async Task Reserve_AcrossFiscalYears_UsesIndependentCounters()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var year1a = await ReserveAsync(db, service, "2025-26");
        var year1b = await ReserveAsync(db, service, "2025-26");
        var year2a = await ReserveAsync(db, service, "2026-27");

        Assert.Equal(1L, year1a.ReservedValue);
        Assert.Equal(2L, year1b.ReservedValue);
        Assert.Equal(1L, year2a.ReservedValue);
        Assert.Contains("2025-26/0002", year1b.FormattedNumber);
        Assert.Contains("2026-27/0001", year2a.FormattedNumber);
    }

    [Fact]
    public async Task Preview_UsesCloudSettingsPrefixAndSuffix()
    {
        await using var db = CreateDbContext();
        db.CloudSettings.Add(new CloudSettingsEntity
        {
            Id = Guid.NewGuid(),
            Host = "localhost",
            Port = 9000,
            TimeoutSeconds = 10,
            ActiveCompanyName = "ACME",
            PrintCompanyName = "ACME",
            InvoicePrefix = "INV-",
            InvoiceSuffix = "/A",
            SalesLedger = "Sales",
            CashLedger = "Cash",
            CreditDebitLedger = "Debtors",
            CgstLedger = "CGST",
            SgstLedger = "SGST",
            RoundOffLedger = "RoundOff",
            DiscountLedger = "Disc",
            SalesVoucherType = "Sales",
            ItemMasterDataJson = "[]",
            KaratMappingDataJson = "[]",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var service = new NumberingService(db);
        var preview = await service.GetPreviewAsync("2026-27", CancellationToken.None);

        Assert.Equal("INV-", preview.Prefix);
        Assert.Equal("/A", preview.Suffix);
        Assert.Equal("INV-2026-27/0001/A", preview.FormattedNumber);
    }

    [Fact]
    public void ComputeCurrentFiscalYear_AprilFirst_RollsOver()
    {
        // 23:59 IST on 31 March is 18:29 UTC; 00:30 IST on 1 April is still
        // 31 March in UTC. The year must turn over on the IST calendar.
        var march = InvoiceNumberFormatter.ComputeFiscalYear(new DateTimeOffset(2026, 3, 31, 18, 29, 0, TimeSpan.Zero));
        var april = InvoiceNumberFormatter.ComputeFiscalYear(new DateTimeOffset(2026, 3, 31, 19, 0, 0, TimeSpan.Zero));

        Assert.Equal("2025-26", march);
        Assert.Equal("2026-27", april);
    }

    [Fact]
    public async Task Reserve_OutsideATransaction_IsRefused()
    {
        // The number and its bill commit together, and the sequence lock lasts
        // only as long as the transaction: a caller without one is a mistake.
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReserveAsync("2026-27"));
        Assert.Empty(await db.InvoiceSequences.ToListAsync());
    }

    /// <summary>Reserves as the bill workflows do: inside a transaction, saved and committed by the caller.</summary>
    private static async Task<ReservedNumber> ReserveAsync(
        ShowroomBilling.Infrastructure.Persistence.ShowroomBillingDbContext db,
        NumberingService service,
        string fiscalYear)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var reserved = await service.ReserveAsync(fiscalYear);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return reserved;
    }

    [Fact]
    public async Task Reserve_SkipsOverExistingBillInvoiceNumber()
    {
        await using var db = CreateDbContext();
        db.Bills.Add(new BillEntity
        {
            Id = Guid.NewGuid(),
            FiscalYear = "2026-27",
            InvoiceNumber = "2026-27/0002",
            InvoiceCore = 2,
            State = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new NumberingService(db);
        var first = await ReserveAsync(db, service, "2026-27");
        var second = await ReserveAsync(db, service, "2026-27");

        Assert.Equal(1L, first.ReservedValue);
        Assert.Equal(3L, second.ReservedValue);
        Assert.Contains("2026-27/0003", second.FormattedNumber);
    }

    [Fact]
    public async Task Preview_ReflectsSkipOverOccupied()
    {
        await using var db = CreateDbContext();
        db.Bills.Add(new BillEntity
        {
            Id = Guid.NewGuid(),
            FiscalYear = "2026-27",
            InvoiceNumber = "2026-27/0002",
            InvoiceCore = 2,
            State = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new NumberingService(db);
        var before = await service.GetPreviewAsync("2026-27", CancellationToken.None);
        Assert.Equal(1L, before.PreviewValue);

        await ReserveAsync(db, service, "2026-27");

        var after = await service.GetPreviewAsync("2026-27", CancellationToken.None);
        Assert.Equal(3L, after.PreviewValue);
    }
}
