using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Persistence.Migrations;
using static ShowroomBilling.Tests.Postgres.MigrationSql;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that drops the eleven unread columns must copy their values
/// aside before it drops them, and must be able to put them back.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DropUnreadColumnsMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261114074137_UniqueInvoiceCore";
    private static readonly DateTimeOffset At = new(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dropped_values_are_copied_aside_first_and_come_back_on_the_way_down()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var (billId, revisionId, untouchedBillId) = await SeedAsync(database);
        var counterId = Guid.NewGuid();

        // Back to the schema that still has the columns, and give them values
        // the way the previous build did.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        Assert.Equal("sales", await ScalarAsync<string>(database, $"""SELECT "BillType" FROM public.bills WHERE "Id" = '{billId}'"""));
        await ExecuteAsync(database,
            $"""UPDATE public.bills SET "CounterId" = '{counterId}', "VoidedAtUtc" = '{At:O}' WHERE "Id" = '{billId}'""");
        await ExecuteAsync(database,
            $"""UPDATE public.bill_revisions SET "SubmittedAtUtc" = '{At:O}', "FinalizedAtUtc" = '{At.AddMinutes(1):O}' WHERE "Id" = '{revisionId}'""");
        await ExecuteAsync(database, """
            UPDATE public.tally_ledger_snapshots
            SET "Parent" = 'Sales Accounts', "PrimaryGroup" = 'Sales Accounts', "IsRevenue" = true, "Gstin" = 'GSTIN-A'
            WHERE "Name" = 'Sales A/c'
            """);
        await ExecuteAsync(database,
            """UPDATE public.tally_voucher_type_snapshots SET "ParentType" = 'Sales', "IsDeemedPositive" = true WHERE "Name" = 'Sales'""");

        await MigrateAsync(database, target: null);

        // Copied aside: only the rows that held something.
        Assert.Equal(DropUnreadColumns.Backups.Order(), (await ExistingBackupsAsync(database)).Order());
        Assert.All(DropUnreadColumns.Backups, name => Assert.DoesNotContain(name, MasterSnapshotsInOneTable.Backups));
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.dropped_20261114_bills"));
        Assert.Equal(counterId, await ScalarAsync<Guid>(database,
            $"""SELECT "CounterId" FROM public.dropped_20261114_bills WHERE "Id" = '{billId}'"""));
        Assert.Equal(At.UtcDateTime, await ScalarAsync<DateTime>(database,
            $"""SELECT "VoidedAtUtc" FROM public.dropped_20261114_bills WHERE "Id" = '{billId}'"""));
        Assert.Equal(At.AddMinutes(1).UtcDateTime, await ScalarAsync<DateTime>(database,
            $"""SELECT "FinalizedAtUtc" FROM public.dropped_20261114_bill_revisions WHERE "Id" = '{revisionId}'"""));
        Assert.Equal("Sales Accounts", await ScalarAsync<string>(database,
            """SELECT "Parent" FROM public.dropped_20261114_tally_ledger_snapshots"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM public.dropped_20261114_tally_voucher_type_snapshots WHERE "IsDeemedPositive" """));

        // Dropped: the eleven columns.
        Assert.Equal(0L, await DroppedColumnCountAsync(database));

        // The app still works on the new schema.
        await using (var db = database.CreateContext())
        {
            Assert.Equal(2, await db.Bills.AsNoTracking().CountAsync());
            Assert.Equal(2, (await new MasterSnapshotService(db).GetLedgersAsync()).Ledgers.Count);
            Assert.Equal("Sales", Assert.Single((await new MasterSnapshotService(db).GetVoucherTypesAsync()).VoucherTypes).Name);
        }

        // And the way back restores every value and removes the copies.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        Assert.Equal(11L, await DroppedColumnCountAsync(database));
        Assert.Equal(counterId, await ScalarAsync<Guid>(database, $"""SELECT "CounterId" FROM public.bills WHERE "Id" = '{billId}'"""));
        Assert.Equal(At.UtcDateTime, await ScalarAsync<DateTime>(database,
            $"""SELECT "VoidedAtUtc" FROM public.bills WHERE "Id" = '{billId}'"""));
        Assert.Equal(At.UtcDateTime, await ScalarAsync<DateTime>(database,
            $"""SELECT "SubmittedAtUtc" FROM public.bill_revisions WHERE "Id" = '{revisionId}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM public.tally_ledger_snapshots
            WHERE "Parent" = 'Sales Accounts' AND "PrimaryGroup" = 'Sales Accounts' AND "IsRevenue" AND "Gstin" = 'GSTIN-A'
            """));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM public.tally_ledger_snapshots WHERE "Parent" IS NULL AND NOT "IsRevenue" """));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM public.tally_voucher_type_snapshots WHERE "ParentType" = 'Sales' AND "IsDeemedPositive" """));

        // A row that held nothing gets what every row was created with.
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""
            SELECT count(*) FROM public.bills
            WHERE "Id" = '{untouchedBillId}' AND "BillType" = 'sales' AND "CounterId" IS NULL AND "VoidedAtUtc" IS NULL
            """));

        await MigrateAsync(database, target: null);
    }

    /// <summary>A database someone has already taken a column out of must still migrate, and still keep the rest.</summary>
    [Fact]
    public async Task A_column_that_is_already_gone_is_left_out_of_the_copy()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var (billId, _, _) = await SeedAsync(database);
        await MigrateAsync(database, Previous);
        await ExecuteAsync(database, """ALTER TABLE public.bills DROP COLUMN "CounterId" """);
        await ExecuteAsync(database, $"""UPDATE public.bills SET "VoidedAtUtc" = '{At:O}' WHERE "Id" = '{billId}'""");

        await MigrateAsync(database, target: null);

        Assert.Equal(0L, await DroppedColumnCountAsync(database));
        Assert.Equal(At.UtcDateTime, await ScalarAsync<DateTime>(database,
            $"""SELECT "VoidedAtUtc" FROM public.dropped_20261114_bills WHERE "Id" = '{billId}'"""));

        await MigrateAsync(database, Previous);
        Assert.Equal(11L, await DroppedColumnCountAsync(database));
        Assert.Equal(At.UtcDateTime, await ScalarAsync<DateTime>(database,
            $"""SELECT "VoidedAtUtc" FROM public.bills WHERE "Id" = '{billId}'"""));
    }

    private static async Task<(Guid BillId, Guid RevisionId, Guid UntouchedBillId)> SeedAsync(PostgresFixture.PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var service = Lifecycle(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var untouched = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        var masters = new MasterSnapshotService(db);
        await masters.IngestLedgersAsync([new LedgerSnapshotItem("Sales A/c"), new LedgerSnapshotItem("Cash")]);
        await masters.IngestVoucherTypesAsync([new VoucherTypeSnapshotItem("Sales")]);
        return (bill.Id, bill.CurrentRevisionId!.Value, untouched.Id);
    }

    private static Task<long> DroppedColumnCountAsync(PostgresFixture.PostgresTestDatabase database) =>
        ScalarAsync<long>(database, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND ((table_name = 'bills' AND column_name IN ('CounterId', 'BillType', 'VoidedAtUtc'))
                OR (table_name = 'bill_revisions' AND column_name IN ('SubmittedAtUtc', 'FinalizedAtUtc'))
                OR (table_name = 'tally_ledger_snapshots' AND column_name IN ('Parent', 'PrimaryGroup', 'IsRevenue', 'Gstin'))
                OR (table_name = 'tally_voucher_type_snapshots' AND column_name IN ('ParentType', 'IsDeemedPositive')))
            """);

    private static Task<List<string>> ExistingBackupsAsync(PostgresFixture.PostgresTestDatabase database) =>
        // The copies of the migration under test; MasterSnapshotsInOneTable's own end in _rows.
        ReadNamesAsync(database, """
            SELECT tablename FROM pg_tables
            WHERE schemaname = 'public' AND tablename LIKE 'dropped\_20261114\_%' AND tablename NOT LIKE '%\_rows'
            """);
}
