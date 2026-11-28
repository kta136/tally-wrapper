using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class SaveRoundTripPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreateDraft_SavesInOneBatchedWriteAfterItsLookups()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var warm = database.CreateContext())
        {
            await Lifecycle(warm)
                .CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("warm", 100m), Guid.NewGuid()));
        }

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>(database.Options)
            .AddInterceptors(counter)
            .Options;
        await using var db = new ShowroomBillingDbContext(options);
        await Lifecycle(db)
            .CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m), Guid.NewGuid()));

        // op-id lookup, reservation-key lookup, sequence FOR UPDATE, numbering
        // settings, occupancy window, one batched INSERT. The commit is extra.
        Assert.True(
            counter.Commands.Count <= 6,
            $"Save used {counter.Commands.Count} commands: " + string.Join(" | ", counter.Commands.Select(c => c.Trim().Split((char)10)[0])));
    }
}
