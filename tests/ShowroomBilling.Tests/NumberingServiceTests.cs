using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Tests;

public sealed class NumberingServiceTests
{
    private const string SalesInvoice = INumberingService.DocumentTypeSalesInvoice;

    [Fact]
    public async Task Preview_WithNoSequence_ReturnsOneAndDoesNotReserve()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var preview1 = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);
        var preview2 = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);

        Assert.Equal(1L, preview1.PreviewValue);
        Assert.Equal(1L, preview2.PreviewValue);
        Assert.Empty(await db.InvoiceNumberReservations.ToListAsync());
        Assert.Empty(await db.InvoiceSequences.ToListAsync());
    }

    [Fact]
    public async Task Reserve_AllocatesSequentialValues()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var first = await service.ReserveAsync(new ReserveNumberRequest("k-1", SalesInvoice, "2026-27", null));
        var second = await service.ReserveAsync(new ReserveNumberRequest("k-2", SalesInvoice, "2026-27", null));
        var third = await service.ReserveAsync(new ReserveNumberRequest("k-3", SalesInvoice, "2026-27", null));

        Assert.Equal(1L, first.ReservedValue);
        Assert.Equal(2L, second.ReservedValue);
        Assert.Equal(3L, third.ReservedValue);
        Assert.False(first.AlreadyExisted);
        Assert.Contains("2026-27/0003", third.FormattedNumber);
    }

    [Fact]
    public async Task Reserve_SameIdempotencyKey_ReturnsSameValue()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var first = await service.ReserveAsync(new ReserveNumberRequest("key-abc", SalesInvoice, "2026-27", "bill-1"));
        var second = await service.ReserveAsync(new ReserveNumberRequest("key-abc", SalesInvoice, "2026-27", "bill-1"));

        Assert.Equal(first.ReservedValue, second.ReservedValue);
        Assert.Equal(first.ReservationId, second.ReservationId);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Single(await db.InvoiceNumberReservations.ToListAsync());
    }

    [Fact]
    public async Task Reserve_AdvancesSequence_SoPreviewReflectsNext()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await service.ReserveAsync(new ReserveNumberRequest("k-1", SalesInvoice, "2026-27", null));
        await service.ReserveAsync(new ReserveNumberRequest("k-2", SalesInvoice, "2026-27", null));

        var preview = await service.GetPreviewAsync(SalesInvoice, "2026-27", CancellationToken.None);

        Assert.Equal(3L, preview.PreviewValue);
    }

    [Fact]
    public async Task Reserve_AcrossFiscalYears_UsesIndependentCounters()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        var year1a = await service.ReserveAsync(new ReserveNumberRequest("y1-a", SalesInvoice, "2025-26", null));
        var year1b = await service.ReserveAsync(new ReserveNumberRequest("y1-b", SalesInvoice, "2025-26", null));
        var year2a = await service.ReserveAsync(new ReserveNumberRequest("y2-a", SalesInvoice, "2026-27", null));

        Assert.Equal(1L, year1a.ReservedValue);
        Assert.Equal(2L, year1b.ReservedValue);
        Assert.Equal(1L, year2a.ReservedValue);
        Assert.Contains("2025-26/0002", year1b.FormattedNumber);
        Assert.Contains("2026-27/0001", year2a.FormattedNumber);
    }

    [Fact]
    public async Task Reserve_WritesAuditEvent()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await service.ReserveAsync(new ReserveNumberRequest("k-audit", SalesInvoice, "2026-27", null));

        var audits = await db.AuditEvents.ToListAsync();
        Assert.Contains(audits, a => a.EventType == $"numbering.{SalesInvoice}.reserved" && a.EntityType == "numbering");
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
        var march = NumberingService.ComputeCurrentFiscalYear(new DateTimeOffset(2026, 3, 31, 23, 0, 0, TimeSpan.Zero));
        var april = NumberingService.ComputeCurrentFiscalYear(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal("2025-26", march);
        Assert.Equal("2026-27", april);
    }

    [Fact]
    public async Task Scopes_ListsReservedScopesWithCurrentFiscalYear()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await service.ReserveAsync(new ReserveNumberRequest("k-1", SalesInvoice, "2026-27", null));
        await service.ReserveAsync(new ReserveNumberRequest("k-2", SalesInvoice, "2025-26", null));

        var scopes = await service.GetScopesAsync();

        Assert.Equal(2, scopes.Scopes.Count);
        Assert.False(string.IsNullOrWhiteSpace(scopes.CurrentFiscalYear));
        Assert.Contains(scopes.Scopes, s => s.FiscalYear == "2026-27" && s.NextValue == 2L);
        Assert.Contains(scopes.Scopes, s => s.FiscalYear == "2025-26" && s.NextValue == 2L);
    }

    [Fact]
    public async Task Reserve_InvalidDocumentType_Throws()
    {
        await using var db = CreateDbContext();
        var service = new NumberingService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReserveAsync(new ReserveNumberRequest("k", "unknown_type", "2026-27", null)));
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
        var first = await service.ReserveAsync(new ReserveNumberRequest("skip-1", SalesInvoice, "2026-27", null));
        var second = await service.ReserveAsync(new ReserveNumberRequest("skip-2", SalesInvoice, "2026-27", null));

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

        await service.ReserveAsync(new ReserveNumberRequest("preview-skip", SalesInvoice, "2026-27", null));

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

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }
}
