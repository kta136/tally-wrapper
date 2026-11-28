using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Masters;
using ShowroomBilling.Infrastructure.Persistence.Migrations;
using static ShowroomBilling.Tests.Postgres.MigrationSql;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class RecoveryTableCleanupPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Bills_and_master_lists_survive_rollback_and_upgrade_without_recovery_tables()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await fixture.CreateDatabaseAsync(cancellationToken);
        CompanySnapshotItem[] companies = [new("ACME", true), new("Town B Co", false)];
        StockItemSnapshotItem[] stock = [new("Gold Ring", "gm", "7113")];
        var payload = SamplePayload("Walk-in", 1234.5m);
        Guid billId;
        await using (var db = database.CreateContext())
        {
            var masters = new MasterSnapshotService(db);
            await masters.IngestCompaniesAsync(companies, cancellationToken);
            await masters.IngestStockItemsAsync(stock, cancellationToken);
            var bill = await Lifecycle(db).CreateDraftAsync(new CreateBillDraftRequest(payload), cancellationToken);
            billId = bill.Id;
        }

        // Remove the October and November copies from this fixture's disposable database.
        string[] recoveryTables =
        [
            .. DropUnusedColumnsAndIndexes.Backups,
            DropDraftEditLeases.Backup,
            DropInvoiceNumberReservations.Backup,
            .. DropUnreadColumns.Backups,
            .. MasterSnapshotsInOneTable.Backups,
            .. DropShowroomAndDocumentType.Backups
        ];
        var qualifiedNames = recoveryTables.Select(name => $"public.\"{name}\"");
        await ExecuteAsync(database, $"DROP TABLE IF EXISTS {string.Join(", ", qualifiedNames)} RESTRICT");
        await MigrateAsync(database, "20261009161104_PerformanceReadOptimizationsApplied");

        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.draft_edit_leases"));
        Assert.Equal(0L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.invoice_number_reservations"));
        Assert.Equal("{}", await ScalarAsync<string>(database,
            $"""SELECT "TotalsJson"::text FROM public.bill_revisions WHERE "BillId" = '{billId}'"""));
        Assert.Equal(2L, await ScalarAsync<long>(database, """SELECT count(*) FROM public.tally_master_snapshot_batches WHERE "Status" = 'active'"""));
        Assert.Equal(1L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM public.tally_company_snapshots AS item
            JOIN public.tally_master_snapshot_batches AS batch ON batch."Id" = item."BatchId"
            WHERE item."Name" = 'Town B Co' AND NOT item."IsActive" AND batch."MasterType" = 'companies'
              AND item."ShowroomId" = '9b961fc2-035f-3dd3-43e0-4f8f136e7682'
            """));
        Assert.Equal("7113", await ScalarAsync<string>(database,
            """SELECT "HsnCode" FROM public.tally_stock_item_snapshots WHERE "Name" = 'Gold Ring' AND "BaseUnit" = 'gm'"""));

        await MigrateAsync(database, target: null);
        foreach (var table in new[] { "draft_edit_leases", "invoice_number_reservations", DropDraftEditLeases.Backup, DropInvoiceNumberReservations.Backup })
        {
            Assert.False(await TableExistsAsync(database, table), table);
        }
        await using var current = database.CreateContext();
        var restoredBill = await new BillReadWorkflow(current).GetAsync(billId, cancellationToken);
        Assert.Equal(payload.Totals, restoredBill?.CurrentRevision?.Payload.Totals);
        var restoredMasters = new MasterSnapshotService(current);
        Assert.Equal(companies, (await restoredMasters.GetCompaniesAsync(cancellationToken)).Companies);
        Assert.Equal(stock, (await restoredMasters.GetStockItemsAsync(cancellationToken)).StockItems);
    }
}
