using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class BillWorkflowPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DeleteAsync_TrailingBill_RollsBackSequenceAndReusesFreedCore()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var service = PostgresBillTestSupport.BuildService(db);

        await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));
        await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("B", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("C", 300m)));

        await service.DeleteAsync(third.Id, new DeleteBillRequest("test cleanup", DryRun: false));

        var next = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("D", 400m)));

        Assert.Contains("/0003", next.InvoiceNumber);
        Assert.Equal(4L, await db.InvoiceSequences.Select(static x => x.NextValue).SingleAsync());
    }

    [Fact]
    public async Task PushAsync_EditLandsDuringPreflight_DoesNotPushTheOlderRevision()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var setupDb = database.CreateContext();
        var bill = await PostgresBillTestSupport.BuildService(setupDb)
            .CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));

        await using var pushDb = database.CreateContext();
        await using var editDb = database.CreateContext();
        var poster = new RecordingTallyPoster();
        var health = new BlockingTallyHealth();
        var pushService = PostgresBillTestSupport.BuildService(pushDb, poster, health);
        var editService = PostgresBillTestSupport.BuildService(editDb);

        var push = pushService.PushAsync(bill.Id, new PushBillRequest("push"));
        await health.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await editService.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(PostgresBillTestSupport.SamplePayload("A", 200m)));
        health.Release();

        await Assert.ThrowsAsync<BillStateConflictException>(() => push.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, poster.CallCount);
        await using var verify = database.CreateContext();
        Assert.Equal(BillStates.Pending, await verify.Bills.Select(static x => x.State).SingleAsync());
    }

    [Fact]
    public async Task StaleBillRow_SavedAfterAnotherTransition_IsRejected()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var setupDb = database.CreateContext();
        var bill = await PostgresBillTestSupport.BuildService(setupDb)
            .CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));

        await using var staleDb = database.CreateContext();
        var stale = await staleDb.Bills.SingleAsync(x => x.Id == bill.Id);

        await using var otherDb = database.CreateContext();
        await PostgresBillTestSupport.BuildService(otherDb, new RecordingTallyPoster())
            .PushAsync(bill.Id, new PushBillRequest("push"));

        stale.State = BillStates.Voided;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        await using var verify = database.CreateContext();
        Assert.Equal(BillStates.Posted, await verify.Bills.Select(static x => x.State).SingleAsync());
    }

    [Fact]
    public async Task CreateDraftAsync_ConcurrentReplaysOfOneSave_CreateOneBill()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m), Guid.NewGuid());

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using var db = database.CreateContext();
                return await PostgresBillTestSupport.BuildService(db).CreateDraftAsync(request);
            }))
            .ToArray();
        start.SetResult();
        var responses = await Task.WhenAll(tasks);

        Assert.Single(responses.Select(static x => x.Id).Distinct());
        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.Bills.CountAsync());
        Assert.Equal(2L, await verify.InvoiceSequences.Select(static x => x.NextValue).SingleAsync());
    }

    [Fact]
    public async Task SearchAsync_MatchesPartyNameRegardlessOfCase()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var service = PostgresBillTestSupport.BuildService(db);
        var alpha = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("Alpha Jewels", 100m)));
        await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("Beta 100% Gold", 100m)));

        var found = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null, Search: "alpha jew"));
        var literalPercent = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null, Search: "%"));

        Assert.Equal(alpha.Id, Assert.Single(found.Items).Id);
        Assert.Equal("Beta 100% Gold", Assert.Single(literalPercent.Items).PartyName);
    }

    [Fact]
    public async Task PushAsync_OverlappingCalls_PostsToTallyOnlyOnce()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var setupDb = database.CreateContext())
        {
            var setupService = PostgresBillTestSupport.BuildService(setupDb);
            var bill = await setupService.CreateDraftAsync(
                new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));

            await using var firstDb = database.CreateContext();
            await using var secondDb = database.CreateContext();
            var poster = new BlockingTallyPoster();
            var firstService = PostgresBillTestSupport.BuildService(firstDb, poster);
            var secondService = PostgresBillTestSupport.BuildService(secondDb, poster);

            var firstPush = firstService.PushAsync(bill.Id, new PushBillRequest("first"));
            await poster.FirstRequest.WaitAsync(TimeSpan.FromSeconds(10));

            var secondPush = await secondService.PushAsync(bill.Id, new PushBillRequest("second"));
            Assert.Equal(BillStates.Posting, secondPush.State);
            Assert.Equal(1, poster.CallCount);

            poster.ReleasePosted(tallyMasterId: "101");
            var firstResult = await firstPush.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(BillStates.Posted, firstResult.State);
            Assert.Equal(1, poster.CallCount);
        }

        await using var verify = database.CreateContext();
        var finalState = await verify.Bills.Select(static x => x.State).SingleAsync();
        Assert.Equal(BillStates.Posted, finalState);
    }
}
