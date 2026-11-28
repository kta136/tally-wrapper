using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShowroomBilling.Infrastructure.Persistence.Migrations;
using static ShowroomBilling.Tests.Postgres.MigrationSql;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// The migration that drops <c>draft_edit_leases</c> must copy any rows aside
/// before it drops the table, and must be able to put table and rows back.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DropDraftEditLeasesMigrationPostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261010124946_DropUnusedColumnsAndIndexes";
    private const string Backup = DropDraftEditLeases.Backup;

    [Fact]
    public async Task Rows_are_copied_aside_first_and_come_back_on_the_way_down()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await MigrateAsync(database, Previous);
        var live = Guid.NewGuid();
        var released = Guid.NewGuid();
        var bill = Guid.NewGuid();
        await ExecuteAsync(database, $"""
            INSERT INTO public.draft_edit_leases
                ("Id", "BillId", "ShowroomId", "OwnerActorId", "OwnerCounterName", "AcquiredAtUtc", "ExpiresAtUtc", "RenewedAtUtc", "ReleasedAtUtc")
            VALUES
                ('{live}', '{bill}', '{Guid.NewGuid()}', 'counter-a', 'Counter A', now(), now() + interval '2 minutes', now(), NULL),
                ('{released}', '{bill}', '{Guid.NewGuid()}', 'counter-b', NULL, now() - interval '1 hour', now() - interval '58 minutes', now() - interval '1 hour', now() - interval '59 minutes')
            """, expectedRows: 2);

        await MigrateAsync(database, target: null);

        Assert.False(await TableExistsAsync(database, "draft_edit_leases"));
        Assert.Equal(2L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public."{Backup}" """));
        Assert.Equal("counter-a", await ScalarAsync<string>(database,
            $"""SELECT "OwnerActorId" FROM public."{Backup}" WHERE "Id" = '{live}'"""));

        // The app runs on the new schema.
        await using (var db = database.CreateContext())
        {
            Assert.Equal(0, await db.AdminSessions.CountAsync());
        }

        // And the way back restores the table, its rows and its three indexes, and removes the copy.
        await MigrateAsync(database, Previous);
        Assert.False(await TableExistsAsync(database, Backup));
        Assert.Equal(2L, await ScalarAsync<long>(database, "SELECT count(*) FROM public.draft_edit_leases"));
        Assert.Equal("Counter A", await ScalarAsync<string>(database,
            $"""SELECT "OwnerCounterName" FROM public.draft_edit_leases WHERE "Id" = '{live}'"""));
        Assert.Equal(3L, await ScalarAsync<long>(database, """
            SELECT count(*) FROM pg_indexes
            WHERE schemaname = 'public'
              AND indexname IN ('IX_draft_edit_leases_BillId', 'IX_draft_edit_leases_ExpiresAtUtc', 'IX_draft_edit_leases_ShowroomId_BillId')
            """));
        // The unique index is the partial one: a second live lease on the same bill is refused.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, $"""
            INSERT INTO public.draft_edit_leases
                ("Id", "BillId", "ShowroomId", "OwnerActorId", "AcquiredAtUtc", "ExpiresAtUtc", "RenewedAtUtc")
            VALUES ('{Guid.NewGuid()}', '{bill}', '{Guid.NewGuid()}', 'counter-c', now(), now(), now())
            """, expectedRows: 1));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        await MigrateAsync(database, target: null);
        Assert.Equal(2L, await ScalarAsync<long>(database, $"""SELECT count(*) FROM public."{Backup}" """));
    }
}
