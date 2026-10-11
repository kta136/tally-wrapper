using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Persistence.Migrations;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that drops unused columns must copy their values aside
/// before it drops them, and must be able to put them back.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DropUnusedColumnsMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261009161104_PerformanceReadOptimizationsApplied";
    private const string Totals = """{"grandTotal": 1234.5}""";
    private const string RawXml = """{"xml": "<COMPANY NAME=\"ACME\"/>"}""";

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Dropped_values_are_copied_aside_first_and_come_back_on_the_way_down()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        Guid billId, revisionId;
        var createdBy = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            var bill = await PostgresBillTestSupport.BuildService(db).CreateDraftAsync(
                new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 1234.5m)));
            billId = bill.Id;
            revisionId = bill.CurrentRevisionId!.Value;
            await new MasterSnapshotService(db).IngestCompaniesAsync(new PushCompanySnapshotRequest(
                "DEV-SHOWROOM", DateTimeOffset.UtcNow, [new CompanySnapshotItem("ACME", true), new CompanySnapshotItem("Other", true)]));
        }

        // Back to the schema that still has the columns, and give them values
        // the way the previous build did.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        await ExecuteAsync(database, $"""UPDATE public.bills SET "CreatedBy" = '{createdBy}' WHERE "Id" = '{billId}'""");
        await ExecuteAsync(database, $"""UPDATE public.bill_revisions SET "TotalsJson" = '{Totals}' WHERE "Id" = '{revisionId}'""");
        await ExecuteAsync(database, $"""UPDATE public.tally_company_snapshots SET "RawJson" = '{RawXml}' WHERE "Name" = 'ACME'""");

        await MigrateAsync(database, target: null);

        // Copied aside: only rows that held something.
        Assert.Equal(DropUnusedColumnsAndIndexes.Backups.Order(), (await ExistingBackupsAsync(database)).Order());
        Assert.Equal(createdBy, await ScalarAsync<Guid>(database,
            $"""SELECT "CreatedBy" FROM public.dropped_20261010_bills_created_by WHERE "Id" = '{billId}'"""));
        Assert.Equal(1234.5m, await ScalarAsync<decimal>(database,
            $"""SELECT ("TotalsJson"->>'grandTotal')::numeric FROM public.dropped_20261010_bill_revisions_totals_json WHERE "Id" = '{revisionId}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            "SELECT count(*) FROM public.dropped_20261010_tally_company_snapshots_raw_json"));

        // Dropped: the six columns and the twelve indexes.
        Assert.Equal(0L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND ((table_name = 'bills' AND column_name = 'CreatedBy')
                OR (table_name = 'bill_revisions' AND column_name = 'TotalsJson')
                OR (table_name LIKE 'tally\_%\_snapshots' AND column_name = 'RawJson'))
            """));
        Assert.Equal(0L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM pg_indexes
            WHERE schemaname = 'public'
              AND indexname IN (
                'IX_bills_ShowroomId_State_CreatedAtUtc', 'IX_bill_revisions_BillDate',
                'IX_cloud_settings_UpdatedAtUtc', 'IX_database_identity_value',
                'IX_tally_company_snapshots_BatchId', 'IX_tally_company_snapshots_ShowroomId_Name',
                'IX_tally_ledger_snapshots_BatchId', 'IX_tally_ledger_snapshots_ShowroomId_Name',
                'IX_tally_stock_item_snapshots_BatchId', 'IX_tally_stock_item_snapshots_ShowroomId_Name',
                'IX_tally_voucher_type_snapshots_BatchId', 'IX_tally_voucher_type_snapshots_ShowroomId_Name')
            """));

        // The app still works on the new schema.
        await using (var db = database.CreateContext())
        {
            Assert.Equal(1234.5m, (await db.BillRevisions.AsNoTracking().SingleAsync(x => x.Id == revisionId)).GrandTotal);
            Assert.Equal(2, (await new MasterSnapshotService(db).GetCompaniesAsync()).Companies.Count);
        }

        // And the way back restores every value and removes the copies.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        Assert.Equal(createdBy, await ScalarAsync<Guid>(database, $"""SELECT "CreatedBy" FROM public.bills WHERE "Id" = '{billId}'"""));
        Assert.Equal(1234.5m, await ScalarAsync<decimal>(database,
            $"""SELECT ("TotalsJson"->>'grandTotal')::numeric FROM public.bill_revisions WHERE "Id" = '{revisionId}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM public.tally_company_snapshots WHERE "RawJson"::text <> '{}'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database,
            """SELECT count(*) FROM public.tally_company_snapshots WHERE "RawJson"::text = '{}'"""));

        await MigrateAsync(database, target: null);
    }

    private static async Task MigrateAsync(PostgresFixture.PostgresTestDatabase database, string? target)
    {
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(target);
        NpgsqlConnection.ClearAllPools();
    }

    private static async Task<List<string>> ExistingBackupsAsync(PostgresFixture.PostgresTestDatabase database)
    {
        var names = new List<string>();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'dropped\_20261010\_%'""";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task ExecuteAsync(PostgresFixture.PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<T> ScalarAsync<T>(PostgresFixture.PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
