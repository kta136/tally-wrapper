using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class NumberingServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private const string SalesInvoice = NumberingService.DocumentTypeSalesInvoice;

    [Fact]
    public async Task Preview_WithNoSequence_ReturnsOneAndDoesNotReserve()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var preview1 = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);
        var preview2 = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);

        Assert.Equal(1L, preview1.PreviewValue);
        Assert.Equal(1L, preview2.PreviewValue);
        Assert.Empty(await db.InvoiceSequences.ToListAsync());
    }

    [Fact]
    public async Task Reserve_AllocatesSequentialValues()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var first = await service.ReserveAsync(SalesInvoice, "2026-27");
        var second = await service.ReserveAsync(SalesInvoice, "2026-27");
        var third = await service.ReserveAsync(SalesInvoice, "2026-27");

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

        await service.ReserveAsync(SalesInvoice, "2026-27");
        await service.ReserveAsync(SalesInvoice, "2026-27");

        var preview = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);

        Assert.Equal(3L, preview.PreviewValue);
    }

    [Fact]
    public async Task Reserve_AcrossFiscalYears_UsesIndependentCounters()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var year1a = await service.ReserveAsync(SalesInvoice, "2025-26");
        var year1b = await service.ReserveAsync(SalesInvoice, "2025-26");
        var year2a = await service.ReserveAsync(SalesInvoice, "2026-27");

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
        var preview = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);

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
    public async Task Reserve_InvalidDocumentType_Throws()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.ReserveAsync("unknown_type", "2026-27"));
    }

    [Fact]
    public async Task Reserve_SkipsOverExistingBillInvoiceNumber()
    {
        await using var db = CreateDbContext();
        db.Bills.Add(new BillEntity
        {
            Id = Guid.NewGuid(),
            ShowroomId = DefaultShowroomId,
            FiscalYear = "2026-27",
            InvoiceNumber = "2026-27/0002",
            State = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new NumberingService(db);
        var first = await service.ReserveAsync(SalesInvoice, "2026-27");
        var second = await service.ReserveAsync(SalesInvoice, "2026-27");

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
            ShowroomId = DefaultShowroomId,
            FiscalYear = "2026-27",
            InvoiceNumber = "2026-27/0002",
            State = "pending",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new NumberingService(db);
        var before = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);
        Assert.Equal(1L, before.PreviewValue);

        await service.ReserveAsync(SalesInvoice, "2026-27");

        var after = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);
        Assert.Equal(3L, after.PreviewValue);
    }

    private static readonly Guid DefaultShowroomId = ComputeDefaultShowroomId();

    private static Guid ComputeDefaultShowroomId()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("default");
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        return new Guid(hash);
    }
}
