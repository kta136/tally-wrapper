using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Admin;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Migrations;
using static ShowroomBilling.Tests.Postgres.MigrationSql;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that takes <c>ShowroomId</c> and <c>DocumentType</c> out. On a database the
/// application wrote, every row held the same two values and nothing is lost. A database that held
/// anything else must still migrate, keep what it held, and get it back on the way down.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DropShowroomAndDocumentTypeMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261114122406_MasterSnapshotsInOneTable";
    private const string TheShowroom = "9b961fc2-035f-3dd3-43e0-4f8f136e7682";
    private const string Passcode = "111111";

    [Fact]
    public async Task On_a_database_the_application_wrote_nothing_is_copied_and_the_sequence_stands_where_it_stood()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await SeedAsync(database);

        // The way down gives every row the one showroom and the one document type back.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        Assert.Equal(6L, await DroppedColumnCountAsync(database));
        foreach (var table in new[] { "bills", "invoice_sequences", "admin_passcodes", "admin_sessions", "print_assets" })
        {
            Assert.Equal(0L, await ScalarAsync<long>(database,
                $"""SELECT count(*) FROM public."{table}" WHERE "ShowroomId" <> '{TheShowroom}'"""));
        }

        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM public.invoice_sequences WHERE "DocumentType" = 'sales_invoice' AND "NextValue" = 3"""));
        Assert.Equal("ShowroomId, FiscalYear, DocumentType", await SequenceKeyAsync(database));
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_ShowroomId_FiscalYear_InvoiceCore"));
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_ShowroomId_FiscalYear_InvoiceNumber"));
        Assert.True(await IndexIsUniqueAsync(database, "IX_admin_passcodes_ShowroomId"));

        await MigrateAsync(database, target: null);

        // The copies are made and are empty.
        Assert.Equal(DropShowroomAndDocumentType.Backups.Order(), (await ExistingBackupsAsync(database)).Order());
        foreach (var backup in DropShowroomAndDocumentType.Backups)
        {
            Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public."{backup}" """));
        }

        Assert.Equal(0L, await DroppedColumnCountAsync(database));
        Assert.Equal("FiscalYear", await SequenceKeyAsync(database));
        Assert.Equal(3L, await ScalarAsync<long>(database, """SELECT "NextValue" FROM public.invoice_sequences"""));
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_FiscalYear_InvoiceCore"));
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_FiscalYear_InvoiceNumber"));
        Assert.Equal(0L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname LIKE '%ShowroomId%'"""));

        // The application carries on where it was: the next number, and the passcode set before.
        await using var db = database.CreateContext();
        var third = await Lifecycle(db)
            .CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));
        Assert.EndsWith("0003", third.InvoiceNumber);
        Assert.NotNull((await Admin(db).UnlockAsync(new AdminUnlockRequest(Passcode, null))).Token);
    }

    [Fact]
    public async Task Rows_of_another_showroom_are_copied_aside_first_and_come_back_on_the_way_down()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var (firstId, secondId) = await SeedAsync(database);
        var other = Guid.NewGuid();

        // Back to the schema that still has the columns, with what the application never wrote:
        // a bill and a sequence of another showroom, and a sequence of another document type.
        await MigrateAsync(database, Previous);
        var year = await ScalarAsync<string>(database, """SELECT "FiscalYear" FROM public.invoice_sequences""");
        await ExecuteAsync(database, $"""UPDATE public.bills SET "ShowroomId" = '{other}' WHERE "Id" = '{secondId}'""");
        await ExecuteAsync(database, $"""
            INSERT INTO public.invoice_sequences ("ShowroomId", "FiscalYear", "DocumentType", "NextValue", "MinNextValue", "UpdatedAtUtc")
            VALUES ('{other}', '{year}', 'sales_invoice', 40, 7, now()),
                   ('{TheShowroom}', '{year}', 'estimate', 5, 0, now())
            """);

        await MigrateAsync(database, target: null);

        // Copied aside: the one bill, and the sequences whole, because their key changes.
        Assert.Equal(DropShowroomAndDocumentType.Backups.Order(), (await ExistingBackupsAsync(database)).Order());
        Assert.Equal(other, await ScalarAsync<Guid>(database, """SELECT "ShowroomId" FROM public.dropped_20261115_bills"""));
        Assert.Equal(secondId, await ScalarAsync<Guid>(database, """SELECT "Id" FROM public.dropped_20261115_bills"""));
        Assert.Equal(3L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.dropped_20261115_invoice_sequences"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.dropped_20261115_admin_passcodes"));
        Assert.Equal(0L, await DroppedColumnCountAsync(database));

        // One sequence for the year, standing where the furthest of the three stood.
        Assert.Equal("FiscalYear", await SequenceKeyAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT count(*) FROM public.invoice_sequences WHERE "FiscalYear" = '{year}' AND "NextValue" = 40 AND "MinNextValue" = 7"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.invoice_sequences"));

        // The application works on it, and the next number is past every number either showroom issued.
        Guid thirdId;
        await using (var db = database.CreateContext())
        {
            Assert.Equal(2, await db.Bills.AsNoTracking().CountAsync());
            var third = await Lifecycle(db)
                .CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));
            thirdId = third.Id;
            Assert.EndsWith("0040", third.InvoiceNumber);
        }

        // The way back: columns, values, key and indexes, and the copies gone.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        Assert.Equal(6L, await DroppedColumnCountAsync(database));
        Assert.Equal(other, await ScalarAsync<Guid>(database, $"""SELECT "ShowroomId" FROM public.bills WHERE "Id" = '{secondId}'"""));
        Assert.Equal(2L, await ScalarAsync<long>(database,
            $"""SELECT count(*) FROM public.bills WHERE "ShowroomId" = '{TheShowroom}' AND "Id" IN ('{firstId}', '{thirdId}')"""));
        Assert.Equal("ShowroomId, FiscalYear, DocumentType", await SequenceKeyAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT count(*) FROM public.invoice_sequences WHERE "ShowroomId" = '{other}' AND "DocumentType" = 'sales_invoice' AND "NextValue" = 40 AND "MinNextValue" = 7"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT count(*) FROM public.invoice_sequences WHERE "ShowroomId" = '{TheShowroom}' AND "DocumentType" = 'estimate' AND "NextValue" = 5"""));
        // The one showroom's own sequence stays where it stands today: a number has been issued since.
        Assert.Equal(1L, await ScalarAsync<long>(database,
            $"""SELECT count(*) FROM public.invoice_sequences WHERE "ShowroomId" = '{TheShowroom}' AND "DocumentType" = 'sales_invoice' AND "NextValue" = 41"""));
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_ShowroomId_FiscalYear_InvoiceCore"));
        Assert.True(await IndexIsUniqueAsync(database, "IX_admin_passcodes_ShowroomId"));

        await MigrateAsync(database, target: null);
        Assert.Equal(0L, await DroppedColumnCountAsync(database));
    }

    /// <summary>
    /// Two showrooms could each hold number 1 of a year. Without the showroom those are two bills on
    /// one number, and a unique index cannot be built over them: the migration must not fail on it.
    /// </summary>
    [Fact]
    public async Task Two_bills_that_end_up_on_one_number_leave_plain_indexes_and_the_migration_does_not_fail()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var (firstId, secondId) = await SeedAsync(database);

        await MigrateAsync(database, Previous);
        await ExecuteAsync(database, $"""
            UPDATE public.bills AS moved
               SET "ShowroomId" = '{Guid.NewGuid()}', "InvoiceNumber" = first."InvoiceNumber", "InvoiceCore" = first."InvoiceCore"
              FROM public.bills AS first
             WHERE moved."Id" = '{secondId}' AND first."Id" = '{firstId}'
            """);

        await MigrateAsync(database, target: null);

        Assert.False(await IndexIsUniqueAsync(database, "IX_bills_FiscalYear_InvoiceCore"));
        Assert.False(await IndexIsUniqueAsync(database, "IX_bills_FiscalYear_InvoiceNumber"));
        await using (var db = database.CreateContext())
        {
            Assert.Equal(2, await db.Bills.AsNoTracking().CountAsync());
        }

        // With the showrooms back, the two are apart again and the old indexes are unique.
        await MigrateAsync(database, Previous);
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_ShowroomId_FiscalYear_InvoiceCore"));
        Assert.True(await IndexIsUniqueAsync(database, "IX_bills_ShowroomId_FiscalYear_InvoiceNumber"));
    }

    /// <summary>A database someone has already taken a column out of must still migrate.</summary>
    [Fact]
    public async Task A_column_that_is_already_gone_does_not_stop_it()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await SeedAsync(database);
        await MigrateAsync(database, Previous);
        // Each takes what it was part of with it: an index, and the key of the sequences.
        await ExecuteAsync(database, """ALTER TABLE public.print_assets DROP COLUMN "ShowroomId" """);
        await ExecuteAsync(database, """ALTER TABLE public.invoice_sequences DROP COLUMN "DocumentType" """);

        await MigrateAsync(database, target: null);

        Assert.Equal(0L, await DroppedColumnCountAsync(database));
        Assert.Equal("FiscalYear", await SequenceKeyAsync(database));
        Assert.DoesNotContain("dropped_20261115_print_assets", await ExistingBackupsAsync(database));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_print_assets_AssetKind'"""));

        await MigrateAsync(database, Previous);
        Assert.Equal(6L, await DroppedColumnCountAsync(database));
        Assert.Equal("ShowroomId, FiscalYear, DocumentType", await SequenceKeyAsync(database));
    }

    /// <summary>Two bills, a passcode and a session: a row in each table but the print assets.</summary>
    private static async Task<(Guid FirstId, Guid SecondId)> SeedAsync(PostgresFixture.PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var service = Lifecycle(db);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        await Admin(db).SetPasscodeAsync(new AdminSetPasscodeRequest(null, Passcode));
        await Admin(db).UnlockAsync(new AdminUnlockRequest(Passcode, null));
        return (first.Id, second.Id);
    }

    private static AdminAuthService Admin(ShowroomBillingDbContext db) =>
        new(db, new AdminUnlockThrottle(), NullLogger<AdminAuthService>.Instance);

    private static Task<long> DroppedColumnCountAsync(PostgresFixture.PostgresTestDatabase database) =>
        ScalarAsync<long>(database, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND ((column_name = 'ShowroomId' AND table_name IN ('bills', 'invoice_sequences', 'admin_passcodes', 'admin_sessions', 'print_assets'))
                OR (column_name = 'DocumentType' AND table_name = 'invoice_sequences'))
            """);

    /// <summary>The columns of the sequences' primary key, in order.</summary>
    private static Task<string> SequenceKeyAsync(PostgresFixture.PostgresTestDatabase database) =>
        ScalarAsync<string>(database, """
            SELECT string_agg(a.attname::text, ', ' ORDER BY k.position)
            FROM pg_constraint c
            CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, position)
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
            WHERE c.conrelid = 'public.invoice_sequences'::regclass AND c.contype = 'p'
            """);

    private static Task<bool> IndexIsUniqueAsync(PostgresFixture.PostgresTestDatabase database, string index) =>
        ScalarAsync<bool>(database, $"""
            SELECT i.indisunique
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = '{index}'
            """);

    private static Task<List<string>> ExistingBackupsAsync(PostgresFixture.PostgresTestDatabase database) =>
        ReadNamesAsync(database, """
            SELECT tablename FROM pg_tables
            WHERE schemaname = 'public' AND tablename LIKE 'dropped\_20261115\_%'
            """);
}
