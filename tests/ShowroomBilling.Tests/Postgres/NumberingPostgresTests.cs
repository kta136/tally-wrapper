using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class NumberingPostgresTests(PostgresFixture fixture)
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ReserveAsync_ConcurrentFirstReservations_AllocatesDistinctSequentialValues()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int count = 16;

        var tasks = Enumerable.Range(1, count)
            .Select(i => Task.Run(async () =>
            {
                await start.Task;
                await using var db = database.CreateContext();
                var service = new NumberingService(db);
                return await service.ReserveAsync(new ReserveNumberRequest(
                    IdempotencyKey: $"race:{i}",
                    DocumentType: INumberingService.DocumentTypeSalesInvoice,
                    FiscalYear: "2026-27",
                    ReservedForReference: null));
            }))
            .ToArray();

        start.SetResult();
        var responses = await Task.WhenAll(tasks);

        Assert.Equal(
            Enumerable.Range(1, count).Select(static x => (long)x),
            responses.Select(static x => x.ReservedValue).Order());
        Assert.Equal(count, responses.Select(static x => x.ReservationId).Distinct().Count());
        Assert.All(responses, static response => Assert.False(response.AlreadyExisted));

        await using var verify = database.CreateContext();
        var sequence = await verify.InvoiceSequences.SingleAsync();
        Assert.Equal(count + 1L, sequence.NextValue);
        Assert.Equal(count, await verify.InvoiceNumberReservations.CountAsync());
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ReserveAsync_ConcurrentSameIdempotencyKey_ReturnsSingleReservation()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int count = 12;

        var tasks = Enumerable.Range(1, count)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using var db = database.CreateContext();
                var service = new NumberingService(db);
                return await service.ReserveAsync(new ReserveNumberRequest(
                    IdempotencyKey: "same-key",
                    DocumentType: INumberingService.DocumentTypeSalesInvoice,
                    FiscalYear: "2026-27",
                    ReservedForReference: "same-bill"));
            }))
            .ToArray();

        start.SetResult();
        var responses = await Task.WhenAll(tasks);

        var reservationId = Assert.Single(responses.Select(static x => x.ReservationId).Distinct());
        Assert.All(responses, response => Assert.Equal(reservationId, response.ReservationId));
        Assert.All(responses, static response => Assert.Equal(1L, response.ReservedValue));
        Assert.Equal(count - 1, responses.Count(static response => response.AlreadyExisted));

        await using var verify = database.CreateContext();
        var sequence = await verify.InvoiceSequences.SingleAsync();
        Assert.Equal(2L, sequence.NextValue);
        Assert.Equal(1, await verify.InvoiceNumberReservations.CountAsync());
    }
}
