using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class InvoiceCorePostgresTests(PostgresFixture fixture)
{
    private const string MigrationBeforeInvoiceCore = "20261009153701_BillRowVersion";

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Migration_BackfillsCoreFromReservationThenTrailingDigits()
    {
        await using var database = await fixture.CreateDatabaseAtMigrationAsync(MigrationBeforeInvoiceCore);
        var reserved = Guid.NewGuid();
        var renamed = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        var custom = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                $"""
                INSERT INTO public.bills ("Id", "ShowroomId", "BillType", "State", "InvoiceNumber", "FiscalYear", "CreatedAtUtc", "UpdatedAtUtc", "EditedAfterPush")
                VALUES
                  ('{reserved}', '{Guid.Empty}', 'sales', 'pending', '2026-27/0007-A2', '2026-27', now(), now(), false),
                  ('{renamed}',  '{Guid.Empty}', 'sales', 'pending', '2026-27/0012',    '2026-27', now(), now(), false),
                  ('{legacy}',   '{Guid.Empty}', 'sales', 'pending', 'DDAJR/26-27/49',  '2026-27', now(), now(), false),
                  ('{custom}',   '{Guid.Empty}', 'sales', 'pending', 'LEGACY-X',        '2026-27', now(), now(), false);
                INSERT INTO public.invoice_number_reservations ("Id", "IdempotencyKey", "ShowroomId", "FiscalYear", "DocumentType", "ReservedValue", "FormattedNumber", "ReservedAtUtc")
                VALUES
                  ('{Guid.NewGuid()}', 'draft:{reserved:N}', '{Guid.Empty}', '2026-27', 'sales_invoice', 7, '2026-27/0007-A2', now()),
                  ('{Guid.NewGuid()}', 'draft:{renamed:N}',  '{Guid.Empty}', '2026-27', 'sales_invoice', 3, '2026-27/0003',    now());
                """;
            await insert.ExecuteNonQueryAsync();
        }

        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync();
        }

        await using var verify = database.CreateContext();
        var cores = await verify.Bills.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.InvoiceCore);
        Assert.Equal(7L, cores[reserved]);
        Assert.Equal(12L, cores[renamed]);
        Assert.Equal(49L, cores[legacy]);
        Assert.Null(cores[custom]);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task DeleteTrailingBill_WithLetterSuffix_ReusesFreedCoreWithoutDuplicating()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        await AddSettingsAsync(db, suffix: "-A");
        var service = PostgresBillTestSupport.BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("B", 100m)));
        await service.DeleteAsync(second.Id, new DeleteBillRequest("test", DryRun: false));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("C", 100m)));

        Assert.EndsWith("/0001-A", first.InvoiceNumber);
        Assert.EndsWith("/0002-A", third.InvoiceNumber);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Reserve_WithDigitSuffix_DoesNotMistakeSuffixForCore()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        await AddSettingsAsync(db, suffix: "-A2");
        var service = PostgresBillTestSupport.BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("B", 100m)));

        Assert.EndsWith("/0001-A2", first.InvoiceNumber);
        Assert.EndsWith("/0002-A2", second.InvoiceNumber);
    }

    private static async Task AddSettingsAsync(ShowroomBillingDbContext db, string suffix)
    {
        db.CloudSettings.Add(new CloudSettingsEntity
        {
            Id = Guid.NewGuid(),
            Host = "localhost",
            Port = 9000,
            TimeoutSeconds = 10,
            ActiveCompanyName = "ACME",
            PrintCompanyName = "ACME",
            InvoicePrefix = string.Empty,
            InvoiceSuffix = suffix,
            PrintFontSize = 11,
            PrintTermsFontSize = 9,
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
    }
}
