using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Persistence.Migrations;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that moves the Tally lists into one table must carry each
/// current list across, copy the old tables aside before it drops them, and
/// be able to put them back.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class MasterSnapshotsInOneTableMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261114114013_DropUnreadColumns";
    private const string Showroom = "9b961fc2-035f-3dd3-43e0-4f8f136e7682";

    [Fact]
    public async Task The_current_lists_are_carried_across_and_the_old_tables_come_back_on_the_way_down()
    {
        await using var database = await fixture.CreateDatabaseAtMigrationAsync(Previous);
        var oldBatch = Guid.NewGuid();
        var companies = Guid.NewGuid();
        var stockItems = Guid.NewGuid();
        var emptyLedgers = Guid.NewGuid();
        var ring = Guid.NewGuid();

        // As the previous build left it: a superseded batch beside the active one,
        // a list that came back empty, and a master (voucher types) never fetched.
        await ExecuteAsync(database, $"""
            INSERT INTO public.tally_master_snapshot_batches ("Id", "ShowroomId", "MasterType", "FetchedAtUtc", "Status", "ItemCount")
            VALUES
              ('{oldBatch}',     '{Showroom}', 'companies',   now() - interval '2 days', 'superseded', 1),
              ('{companies}',    '{Showroom}', 'companies',   now() - interval '1 hour', 'active',     2),
              ('{stockItems}',   '{Showroom}', 'stock_items', now() - interval '1 hour', 'active',     1),
              ('{emptyLedgers}', '{Showroom}', 'ledgers',     now() - interval '1 hour', 'active',     0);
            INSERT INTO public.tally_company_snapshots ("Id", "BatchId", "ShowroomId", "Name", "IsActive")
            VALUES
              ('{Guid.NewGuid()}', '{oldBatch}',  '{Showroom}', 'Gone Co',  true),
              ('{Guid.NewGuid()}', '{companies}', '{Showroom}', 'Town B Co', false),
              ('{Guid.NewGuid()}', '{companies}', '{Showroom}', 'ACME',     true);
            INSERT INTO public.tally_stock_item_snapshots ("Id", "BatchId", "ShowroomId", "Name", "Alias", "BaseUnit", "HsnCode", "Karat")
            VALUES ('{ring}', '{stockItems}', '{Showroom}', 'Gold Ring', 'RING-A', 'gm', '7113', NULL);
            """);

        await MigrateAsync(database, target: null);

        // Carried across: the active batch of each master, in name order.
        await using (var db = database.CreateContext())
        {
            var service = new MasterSnapshotService(db);
            var read = await service.GetCompaniesAsync();
            Assert.Equal([new CompanySnapshotItem("ACME", true), new CompanySnapshotItem("Town B Co", false)], read.Companies);
            Assert.Equal(2, read.Metadata.ItemCount);
            Assert.Equal("aging", read.Metadata.Freshness);

            Assert.Equal(new StockItemSnapshotItem("Gold Ring", "gm", "7113"), Assert.Single((await service.GetStockItemsAsync()).StockItems));

            var ledgers = await service.GetLedgersAsync();
            Assert.Empty(ledgers.Ledgers);
            Assert.NotNull(ledgers.Metadata.FetchedAtUtc);

            Assert.Equal("missing", (await service.GetVoucherTypesAsync()).Metadata.Freshness);
        }

        // Copied aside: every old table that held rows, whole.
        Assert.Equal(
            new[] { "tally_company_snapshots", "tally_master_snapshot_batches", "tally_stock_item_snapshots" }
                .Select(table => $"dropped_20261114_{table}_rows").Order(),
            (await ExistingBackupsAsync(database)).Order());
        Assert.All(await ExistingBackupsAsync(database), name => Assert.Contains(name, MasterSnapshotsInOneTable.Backups));
        Assert.Equal(3L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.dropped_20261114_tally_company_snapshots_rows"));
        Assert.Equal("RING-A", await ScalarAsync<string>(database,
            $"""SELECT "Alias" FROM public.dropped_20261114_tally_stock_item_snapshots_rows WHERE "Id" = '{ring}'"""));

        // Dropped: the five old tables.
        Assert.Equal(0L, await OldTableCountAsync(database));

        // And the way back restores every row, the superseded batch included, and removes the copies.
        await MigrateAsync(database, Previous);
        Assert.Empty(await ExistingBackupsAsync(database));
        Assert.Equal(5L, await OldTableCountAsync(database));
        Assert.Equal(4L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.tally_master_snapshot_batches"));
        Assert.Equal(3L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.tally_company_snapshots"));
        Assert.Equal("RING-A", await ScalarAsync<string>(database,
            $"""SELECT "Alias" FROM public.tally_stock_item_snapshots WHERE "Id" = '{ring}' AND "BatchId" = '{stockItems}'"""));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = 'tally_masters'"));

        // The indexes and the cascade are back too.
        Assert.Equal(6L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM pg_indexes
            WHERE schemaname = 'public'
              AND indexname IN (
                'IX_tally_company_snapshots_BatchId_Name', 'IX_tally_ledger_snapshots_BatchId_Name',
                'IX_tally_stock_item_snapshots_BatchId_Name', 'IX_tally_voucher_type_snapshots_BatchId_Name',
                'IX_tally_master_snapshot_batches_ShowroomId_MasterType_Fetched~',
                'IX_tally_master_snapshot_batches_ShowroomId_MasterType_Status')
            """));
        await ExecuteAsync(database, $"""DELETE FROM public.tally_master_snapshot_batches WHERE "Id" = '{companies}'""");
        Assert.Equal(1L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.tally_company_snapshots"));

        await MigrateAsync(database, target: null);
    }

    /// <summary>A database made on the new table has no copies: going down rebuilds the old rows from the lists.</summary>
    [Fact]
    public async Task Without_copies_the_way_down_rebuilds_the_rows_from_the_lists()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var db = database.CreateContext())
        {
            var service = new MasterSnapshotService(db);
            await service.IngestCompaniesAsync([new CompanySnapshotItem("ACME", true), new CompanySnapshotItem("Town B Co", false)]);
            await service.IngestStockItemsAsync([new StockItemSnapshotItem("Gold Ring", "gm", "7113")]);
        }

        await MigrateAsync(database, Previous);

        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.tally_master_snapshot_batches WHERE \"Status\" = 'active'"));
        Assert.Equal(1L, await ScalarAsync<long>(database, $"""
            SELECT count(*) FROM public.tally_company_snapshots AS row
            JOIN public.tally_master_snapshot_batches AS batch ON batch."Id" = row."BatchId"
            WHERE row."Name" = 'Town B Co' AND NOT row."IsActive" AND batch."MasterType" = 'companies' AND row."ShowroomId" = '{Showroom}'
            """));
        Assert.Equal("7113", await ScalarAsync<string>(database,
            """SELECT "HsnCode" FROM public.tally_stock_item_snapshots WHERE "Name" = 'Gold Ring' AND "BaseUnit" = 'gm'"""));

        // And up again, with nothing lost.
        await MigrateAsync(database, target: null);
        await using (var db = database.CreateContext())
        {
            var read = await new MasterSnapshotService(db).GetCompaniesAsync();
            Assert.Equal(["ACME", "Town B Co"], read.Companies.Select(c => c.Name));
        }
    }

    private static Task<long> OldTableCountAsync(PostgresFixture.PostgresTestDatabase database) =>
        ScalarAsync<long>(database, """
            SELECT count(*) FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename IN ('tally_master_snapshot_batches', 'tally_company_snapshots', 'tally_ledger_snapshots',
                                'tally_stock_item_snapshots', 'tally_voucher_type_snapshots')
            """);

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
        command.CommandText = """SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'dropped\_20261114\_%\_rows'""";
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
        await command.ExecuteNonQueryAsync();
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
