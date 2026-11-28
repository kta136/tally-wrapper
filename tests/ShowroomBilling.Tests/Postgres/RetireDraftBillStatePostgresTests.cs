using Npgsql;
using static ShowroomBilling.Tests.Postgres.MigrationSql;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class RetireDraftBillStatePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Upgrade_preserves_legacy_bills_and_prevents_new_drafts()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        const string previous = "20261127141957_AddBankChargesLedger";
        await MigrateAsync(database, previous);
        var id = Guid.NewGuid();
        var edited = Guid.NewGuid();
        await ExecuteAsync(database, $"""
            INSERT INTO public.bills ("Id", "State", "CreatedAtUtc", "UpdatedAtUtc", "EditedAfterPush")
            VALUES ('{id}', 'draft', now(), now(), false), ('{edited}', 'draft', now(), now(), false)
            """, 2);

        await MigrateAsync(database, "20261128100159_RetireDraftBillState");
        Assert.Equal("pending", await ScalarAsync<string>(database, $"""SELECT "State" FROM public.bills WHERE "Id" = '{id}'"""));
        var setDraft = $"""UPDATE public.bills SET "State" = 'draft' WHERE "Id" = '{id}'""";
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database, setDraft));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal("CK_bills_state", failure.ConstraintName);

        await ExecuteAsync(database, $"""UPDATE public.bills SET "UpdatedAtUtc" = "UpdatedAtUtc" + interval '1 hour' WHERE "Id" = '{edited}'""", 1);
        await MigrateAsync(database, previous);
        Assert.Equal("draft", await ScalarAsync<string>(database, $"""SELECT "State" FROM public.bills WHERE "Id" = '{id}'"""));
        Assert.Equal("pending", await ScalarAsync<string>(database, $"""SELECT "State" FROM public.bills WHERE "Id" = '{edited}'"""));
    }
}
