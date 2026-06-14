using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class BillWorkflowPostgresTests(PostgresFixture fixture)
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
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
        Assert.Equal(3L, await db.InvoiceNumberReservations.MaxAsync(static x => x.ReservedValue));
        Assert.Equal(4L, await db.InvoiceSequences.Select(static x => x.NextValue).SingleAsync());
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task ChangeInvoiceNumber_MovesTrailingDown_RollsBackSequenceAndReusesFreedCore()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var service = PostgresBillTestSupport.BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));
        await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("B", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("C", 300m)));
        await service.DeleteAsync(first.Id, new DeleteBillRequest("free first number", DryRun: false));

        var changed = await service.ChangeInvoiceNumberAsync(
            third.Id,
            new ChangeBillNumberRequest("1", "move trailing down", DryRun: false));
        var next = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("D", 400m)));

        Assert.True(changed.Committed);
        Assert.Equal(3L, changed.SequenceNextValue);
        Assert.Contains("/0001", changed.NewInvoiceNumber);
        Assert.Contains("/0003", next.InvoiceNumber);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
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

            var firstPush = firstService.PushAsync(bill.Id, new PushBillRequest(null, "first"));
            await poster.FirstRequest.WaitAsync(TimeSpan.FromSeconds(10));

            var secondPush = await secondService.PushAsync(bill.Id, new PushBillRequest(null, "second"));
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

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task PushAsync_EditAfterPush_PurgesPreEditAuditUsingExecuteDelete()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var db = database.CreateContext();
        var poster = new RecordingTallyPoster(
            PostgresBillTestSupport.PostedResponse(remoteId: "REMOTE-1", tallyMasterId: "101"),
            PostgresBillTestSupport.PostedResponse(remoteId: "REMOTE-2", tallyMasterId: "101"));
        var service = PostgresBillTestSupport.BuildService(db, poster);

        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest(null, "first"));
        await service.UpdateDraftAsync(
            bill.Id,
            new UpdateBillDraftRequest(PostgresBillTestSupport.SamplePayload("A edited", 150m)));

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest(null, "repush-edited"));
        var audit = await service.GetAuditAsync(bill.Id);

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(2, poster.CallCount);
        Assert.NotNull(audit);
        Assert.Equal(
            ["bill.edit.reopened", "bill.push.requested", "tally.posted"],
            audit!.Events.Select(static x => x.EventType).ToArray());
        Assert.Equal(TallyPostOperation.Alter, poster.Requests.Last().Operation);
    }
}
