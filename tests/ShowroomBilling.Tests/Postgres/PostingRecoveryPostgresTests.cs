using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class PostingRecoveryPostgresTests(PostgresFixture fixture)
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task RecoverAsync_OnlyRecoversClaimsOlderThanAnyLivePush()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        Guid abandoned, live;
        await using (var db = database.CreateContext())
        {
            var service = PostgresBillTestSupport.BuildService(db);
            abandoned = (await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)))).Id;
            live = (await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("B", 100m)))).Id;
            await db.Bills.Where(b => b.Id == abandoned).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BillStates.Posting)
                .SetProperty(b => b.UpdatedAtUtc, now.AddMinutes(-20)));
            await db.Bills.Where(b => b.Id == live).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BillStates.Posting)
                .SetProperty(b => b.UpdatedAtUtc, now.AddMinutes(-1)));
        }

        await using var recoveryDb = database.CreateContext();
        var (recovered, nextCheck) = await AbandonedPostingRecovery.RecoverAsync(recoveryDb, now, CancellationToken.None);

        await using var verify = database.CreateContext();
        var states = await verify.Bills.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.State);
        Assert.Equal(1, recovered);
        Assert.Equal(BillStates.ReconciliationRequired, states[abandoned]);
        Assert.Equal(BillStates.Posting, states[live]);
        Assert.NotNull(nextCheck);
        Assert.InRange(nextCheck!.Value, now.AddMinutes(8), now.AddMinutes(10));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(a => a.EventType == "bill.posting.recovered"));
    }
}
