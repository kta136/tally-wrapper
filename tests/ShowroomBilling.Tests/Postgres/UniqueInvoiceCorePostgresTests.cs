using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShowroomBilling.Contracts.Bills;
using static ShowroomBilling.Tests.Postgres.MigrationSql;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// Two bills of one fiscal year cannot hold the same number core, whatever
/// their numbers' text says. The migration that makes the index unique must
/// not fail on a database that already holds such a pair.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class UniqueInvoiceCorePostgresTests(PostgresFixture fixture)
{
    private const string Previous = "20261011114506_DropInvoiceNumberReservations";
    private const string Index = "IX_bills_FiscalYear_InvoiceCore";
    // Its name until the showroom came out of it (DropShowroomAndDocumentType).
    private const string IndexAtPrevious = "IX_bills_ShowroomId_FiscalYear_InvoiceCore";

    [Fact]
    public async Task A_second_bill_cannot_take_a_core_under_other_text()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        Assert.True(await IndexIsUniqueAsync(database));
        await using var db = database.CreateContext();
        var service = Lifecycle(db);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));

        // As after a change of prefix: the same core, formatted to another text.
        await using var other = database.CreateContext();
        var bill = await other.Bills.SingleAsync(x => x.Id == second.Id);
        bill.InvoiceCore = (await other.Bills.AsNoTracking().SingleAsync(x => x.Id == first.Id)).InvoiceCore;
        bill.InvoiceNumber = "NEW/" + first.InvoiceNumber;

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.GetBaseException()).SqlState);
    }

    [Fact]
    public async Task A_database_that_already_holds_two_bills_on_one_core_still_migrates_and_keeps_the_plain_index()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        Guid secondId;
        await using (var db = database.CreateContext())
        {
            var service = Lifecycle(db);
            await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
            secondId = (await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)))).Id;
        }

        // The bills are made first: today's build cannot save one on the older schema.
        await MigrateAsync(database, Previous);
        await ExecuteAsync(database, $"""UPDATE public.bills SET "InvoiceCore" = 1 WHERE "Id" = '{secondId}'""");

        // It must not fail: a migration that fails at start-up leaves the API in limited mode.
        // (Two migrations meet the pair on the way: the one that makes the index unique, and the
        // one that later builds it again without the showroom.)
        await MigrateAsync(database, target: null);

        Assert.False(await IndexIsUniqueAsync(database));

        // Once the pair is dealt with, the index can be made unique by hand, as docs/10 says.
        await ExecuteAsync(database, $"""UPDATE public.bills SET "InvoiceCore" = 2 WHERE "Id" = '{secondId}'""");
        await ExecuteAsync(database, $"""
            DROP INDEX IF EXISTS public."{Index}";
            CREATE UNIQUE INDEX "{Index}" ON public.bills ("FiscalYear", "InvoiceCore") WHERE "InvoiceCore" IS NOT NULL;
            """);
        Assert.True(await IndexIsUniqueAsync(database));
    }

    [Fact]
    public async Task The_way_down_puts_the_plain_index_back_and_the_way_up_makes_it_unique_again()
    {
        await using var database = await fixture.CreateDatabaseAsync();

        await MigrateAsync(database, Previous);
        Assert.False(await IndexIsUniqueAsync(database, IndexAtPrevious));

        await MigrateAsync(database, target: null);
        Assert.True(await IndexIsUniqueAsync(database));
    }

    private static Task<bool> IndexIsUniqueAsync(PostgresFixture.PostgresTestDatabase database, string index = Index) =>
        ScalarAsync<bool>(database, $"""
            SELECT i.indisunique
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = '{index}'
            """);
}
