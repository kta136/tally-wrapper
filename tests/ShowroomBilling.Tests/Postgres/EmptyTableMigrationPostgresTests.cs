using ShowroomBilling.Infrastructure.Persistence.Migrations;
using static ShowroomBilling.Tests.Postgres.MigrationSql;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class EmptyTableMigrationPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("draft_edit_leases", DropDraftEditLeases.Backup, "20261010124946_DropUnusedColumnsAndIndexes")]
    [InlineData("invoice_number_reservations", DropInvoiceNumberReservations.Backup, "20261010132922_DropDraftEditLeases")]
    public async Task An_empty_table_is_dropped_without_a_copy_and_restored_on_rollback(
        string table, string backup, string previousMigration)
    {
        await using var database = await fixture.CreateDatabaseAsync();
        Assert.False(await TableExistsAsync(database, table));
        Assert.False(await TableExistsAsync(database, backup));

        await MigrateAsync(database, previousMigration);
        Assert.True(await TableExistsAsync(database, table));
        Assert.Equal(0L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public."{table}";"""));

        await MigrateAsync(database, target: null);
        Assert.False(await TableExistsAsync(database, table));
        Assert.False(await TableExistsAsync(database, backup));
    }
}
