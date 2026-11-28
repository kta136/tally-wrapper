using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence.Migrations;
using static ShowroomBilling.Tests.Postgres.MigrationSql;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that drops <c>invoice_number_reservations</c> must copy the
/// rows older builds wrote aside before it drops the table, and must be able
/// to put table and rows back.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DropInvoiceNumberReservationsMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261010132922_DropDraftEditLeases";
    private const string Backup = DropInvoiceNumberReservations.Backup;

    [Fact]
    public async Task Rows_are_copied_aside_first_and_come_back_on_the_way_down()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await MigrateAsync(database, Previous);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var showroom = Guid.NewGuid();
        await ExecuteAsync(database, $"""
            INSERT INTO public.invoice_number_reservations
                ("Id", "IdempotencyKey", "ShowroomId", "FiscalYear", "DocumentType", "ReservedValue", "FormattedNumber", "ReservedForReference", "ReservedAtUtc")
            VALUES
                ('{first}', 'draft:{Guid.NewGuid():N}', '{showroom}', '2026-27', 'sales_invoice', 41, 'SR/2026-27/0041', NULL, now() - interval '2 days'),
                ('{second}', 'draft:{Guid.NewGuid():N}', '{showroom}', '2026-27', 'sales_invoice', 42, 'SR/2026-27/0042', 'counter-2', now())
            """, expectedRows: 2);

        await MigrateAsync(database, target: null);

        Assert.False(await TableExistsAsync(database, "invoice_number_reservations"));
        Assert.Equal(2L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public."{Backup}" """));
        Assert.Equal(42L, await ScalarAsync<long>(database,
            $"""SELECT "ReservedValue" FROM public."{Backup}" WHERE "Id" = '{second}'"""));

        // The app saves a bill on the new schema: a number no longer needs the table.
        await using (var db = database.CreateContext())
        {
            var bill = await Lifecycle(db).CreateDraftAsync(
                new CreateBillDraftRequest(SamplePayload("After the drop", 100m)));
            Assert.EndsWith("/0001", bill.InvoiceNumber);
        }

        // And the way back restores the table, its rows and its two indexes, and removes the copy.
        await MigrateAsync(database, Previous);
        Assert.False(await TableExistsAsync(database, Backup));
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.invoice_number_reservations"));
        Assert.Equal("counter-2", await ScalarAsync<string>(database,
            $"""SELECT "ReservedForReference" FROM public.invoice_number_reservations WHERE "Id" = '{second}'"""));
        Assert.Equal(2L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM pg_indexes
            WHERE schemaname = 'public'
              AND indexname IN ('IX_invoice_number_reservations_IdempotencyKey', 'IX_invoice_number_reservations_ShowroomId_FiscalYear_DocumentT~')
            """));
        // The key index is the unique one: an older build relied on it to refuse a second row for one bill.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            INSERT INTO public.invoice_number_reservations
                ("Id", "IdempotencyKey", "ShowroomId", "FiscalYear", "DocumentType", "ReservedValue", "FormattedNumber", "ReservedAtUtc")
            SELECT '{Guid.NewGuid()}', "IdempotencyKey", "ShowroomId", "FiscalYear", "DocumentType", 43, 'SR/2026-27/0043', now()
            FROM public.invoice_number_reservations WHERE "Id" = '{first}'
            """, expectedRows: 1));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        await MigrateAsync(database, target: null);
        Assert.Equal(2L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public."{Backup}" """));
    }
}
