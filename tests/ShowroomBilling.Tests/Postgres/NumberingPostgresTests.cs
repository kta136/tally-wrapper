using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Infrastructure.Numbering;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class NumberingPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReserveAsync_ConcurrentFirstReservations_AllocatesDistinctSequentialValues()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int count = 16;

        var tasks = Enumerable.Range(1, count)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using var db = database.CreateContext();
                var service = new NumberingService(db);
                // As the bill workflows reserve: inside a transaction the caller saves and commits.
                await using var transaction = await db.Database.BeginTransactionAsync();
                var reserved = await service.ReserveAsync("2026-27");
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return reserved;
            }))
            .ToArray();

        start.SetResult();
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(
            Enumerable.Range(1, count).Select(static x => (long)x),
            responses.Select(static x => x.ReservedValue).Order());
        Assert.Equal(count, responses.Select(static x => x.FormattedNumber).Distinct().Count());

        await using var verify = database.CreateContext();
        var sequence = await verify.InvoiceSequences.SingleAsync();
        Assert.Equal(count + 1L, sequence.NextValue);
    }
}
