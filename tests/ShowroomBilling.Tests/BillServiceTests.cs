using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests;

public sealed class BillServiceTests
{
    [Fact]
    public async Task CreateDraft_PersistsBillWithRevisionOneAndReservesSerial()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var response = await service.CreateDraftAsync(new CreateBillDraftRequest(
            CounterId: null,
            Payload: SamplePayload("Walk-in Customer", grandTotal: 1500m)));

        Assert.Equal(IBillService.StatePending, response.State);
        Assert.NotNull(response.CurrentRevision);
        Assert.Equal(1, response.CurrentRevision!.RevisionNo);
        Assert.Equal("Walk-in Customer", response.CurrentRevision.Payload.PartyName);
        Assert.False(string.IsNullOrWhiteSpace(response.InvoiceNumber));
        Assert.Contains("/0001", response.InvoiceNumber);
        Assert.False(string.IsNullOrWhiteSpace(response.FiscalYear));

        Assert.Single(await db.Bills.ToListAsync());
        Assert.Single(await db.BillRevisions.ToListAsync());
        Assert.Single(await db.InvoiceNumberReservations.ToListAsync());
    }

    [Fact]
    public async Task CreateDraft_AssignsConsecutiveSerialsAcrossDrafts()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("B", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("C", 300m)));

        Assert.Contains("/0001", first.InvoiceNumber);
        Assert.Contains("/0002", second.InvoiceNumber);
        Assert.Contains("/0003", third.InvoiceNumber);
        Assert.Equal(3, await db.InvoiceNumberReservations.CountAsync());
    }

    [Fact]
    public async Task UpdateDraft_KeepsInvoiceNumberUnchanged()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var updated = await service.UpdateDraftAsync(created.Id, new UpdateBillDraftRequest(SamplePayload("A-updated", 150m)));

        Assert.Equal(created.InvoiceNumber, updated.InvoiceNumber);
        Assert.Equal(created.FiscalYear, updated.FiscalYear);
        Assert.Single(await db.InvoiceNumberReservations.ToListAsync());
    }

    [Fact]
    public async Task CreateDraft_RoundTripsLineCategoryAndPricingMode()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var payload = new BillPayloadDto(
            PartyName: "Diamond Customer",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Lines:
            [
                new BillLineItemDto(
                    "Diamond Ring",
                    "7113",
                    2m,
                    "ct",
                    3500m,
                    7000m,
                    null,
                    null,
                    DiamondRate: 3500m,
                    ItemCategory: "diamond",
                    PricingMode: "wastage")
            ],
            Totals: new BillTotalsDto(7000m, 0m, 0m, 0m, 7000m),
            Notes: null);

        var response = await service.CreateDraftAsync(new CreateBillDraftRequest(null, payload));

        var line = response.CurrentRevision!.Payload.Lines.Single();
        Assert.Equal("diamond", line.ItemCategory);
        Assert.Equal("wastage", line.PricingMode);
        Assert.Equal(3500m, line.DiamondRate);
    }

    [Fact]
    public async Task Push_DoesNotReserveAnAdditionalNumber()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var draft = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var reservationsBefore = await db.InvoiceNumberReservations.CountAsync();

        var pushed = await service.PushAsync(draft.Id, new PushBillRequest(null, "manual"));

        Assert.Equal(IBillService.StatePosted, pushed.State);
        Assert.Equal(draft.InvoiceNumber, pushed.InvoiceNumber);
        Assert.Equal(draft.FiscalYear, pushed.FiscalYear);
        Assert.Equal(reservationsBefore, await db.InvoiceNumberReservations.CountAsync());
    }

    [Fact]
    public async Task UpdateDraft_AppendsNewRevisionAndPointsCurrentToIt()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var updated = await service.UpdateDraftAsync(created.Id, new UpdateBillDraftRequest(SamplePayload("B", 250m)));

        Assert.Equal(2, updated.CurrentRevision!.RevisionNo);
        Assert.Equal("B", updated.CurrentRevision.Payload.PartyName);
        Assert.NotEqual(created.CurrentRevisionId, updated.CurrentRevisionId);

        var revisions = await db.BillRevisions.Where(x => x.BillId == created.Id).OrderBy(x => x.RevisionNo).ToListAsync();
        Assert.Equal(2, revisions.Count);
        Assert.Equal(updated.CurrentRevisionId, revisions[1].Id);
        Assert.Equal(created.CurrentRevisionId, revisions[1].SupersedesRevisionId);
    }

    [Fact]
    public async Task UpdateDraft_RejectsVoidedBill()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        await service.VoidAsync(bill.Id, new VoidBillRequest("user cancelled"));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("C", 300m))));
    }

    [Fact]
    public async Task UpdateDraft_OnPostedBill_ReopensAsPendingWithEditedAfterPush()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest(null, "sub-1"));

        var updated = await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)));

        Assert.Equal(IBillService.StatePending, updated.State);
        Assert.True(updated.EditedAfterPush);
        Assert.Equal(bill.InvoiceNumber, updated.InvoiceNumber); // invoice number immutable through reopen
    }

    [Fact]
    public async Task Push_CarriesDraftReservedNumberAndTransitionsToPosted()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        var submitted = await service.PushAsync(bill.Id, new PushBillRequest(bill.FiscalYear, "sub-1"));

        Assert.Equal(IBillService.StatePosted, submitted.State);
        Assert.Equal(bill.InvoiceNumber, submitted.InvoiceNumber);
        Assert.Equal(bill.FiscalYear, submitted.FiscalYear);
        Assert.Contains("/0001", submitted.InvoiceNumber);
        Assert.NotNull(submitted.CurrentRevision!.SubmittedAtUtc);
        Assert.NotNull(submitted.CurrentRevision.FinalizedAtUtc);

        Assert.Single(await db.InvoiceNumberReservations.ToListAsync());
    }

    [Fact]
    public async Task Push_OnAlreadyPostedBill_Rejects()
    {
        // Sync push: once a bill is posted, it's a terminal state for normal push.
        // Reposting is a separate workflow (Repost), not a second Submit.
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        await service.PushAsync(bill.Id, new PushBillRequest(null, "first"));
        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest(null, "second")));

        Assert.Single(await db.InvoiceNumberReservations.ToListAsync());
    }

    [Fact]
    public async Task Push_RejectsAlreadyVoidedBill()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        await service.VoidAsync(bill.Id, new VoidBillRequest("mistake"));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest(null, "x")));
    }

    [Fact]
    public async Task Revise_FromDraft_CreatesNewDraftAndMarksPriorAsRevised()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var prior = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        var revised = await service.ReviseAsync(prior.Id, new ReviseBillRequest(InitialPayload: null));

        Assert.NotEqual(prior.Id, revised.Id);
        Assert.Equal(IBillService.StatePending, revised.State);
        Assert.Equal(1, revised.CurrentRevision!.RevisionNo);

        var reloadedPrior = await service.GetAsync(prior.Id);
        Assert.Equal(IBillService.StateRevised, reloadedPrior!.State);
        Assert.Equal(revised.Id, reloadedPrior.SupersededByBillId);
    }

    [Fact]
    public async Task Void_FromDraft_TransitionsToVoidedAndSetsVoidedAt()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        var voided = await service.VoidAsync(bill.Id, new VoidBillRequest("customer cancelled"));

        Assert.Equal(IBillService.StateVoided, voided.State);
        Assert.NotNull(voided.VoidedAtUtc);
    }

    [Fact]
    public async Task Void_RejectsTerminalStates()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        await service.VoidAsync(bill.Id, new VoidBillRequest(null));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.VoidAsync(bill.Id, new VoidBillRequest(null)));
    }

    [Fact]
    public async Task Search_FiltersByStateAndPaginates()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var d1 = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var d2 = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("B", 200m)));
        await service.PushAsync(d2.Id, new PushBillRequest(null, "s2"));
        _ = d1;

        var drafts = await service.SearchAsync(new BillSearchFilter(IBillService.StatePending, null, null, null, null, null));
        var queued = await service.SearchAsync(new BillSearchFilter(IBillService.StatePosted, null, null, null, null, null));

        Assert.Equal(1, drafts.Total);
        Assert.Single(drafts.Items);
        Assert.Equal(1, queued.Total);
        Assert.Single(queued.Items);
        Assert.Equal("B", queued.Items[0].PartyName);
    }

    [Fact]
    public async Task PushSelected_PreservesCallerOrder()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Second", 200m)));

        var response = await service.PushSelectedAsync(new PushSelectedBillsRequest(
            [second.Id, first.Id],
            "2026-27",
            "selected-test"));

        Assert.Equal(2, response.Succeeded);
        Assert.False(response.StoppedOnFailure);
        Assert.Equal(new[] { second.Id, first.Id }, response.Items.Select(x => x.BillId).ToArray());
        Assert.All(response.Items, item => Assert.True(item.Succeeded));
    }

    [Fact]
    public async Task GetMany_PreservesCallerOrderAndSkipsMissingBills()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Second", 200m)));
        var missing = Guid.NewGuid();

        var response = await service.GetManyAsync(new BillBatchGetRequest([second.Id, missing, first.Id]));

        Assert.Equal(new[] { second.Id, first.Id }, response.Bills.Select(x => x.Id).ToArray());
        Assert.All(response.Bills, bill => Assert.NotNull(bill.CurrentRevision));
    }

    [Fact]
    public async Task PushSelected_StopsAtFirstFailure_AndLeavesRemainingBillsUntouched()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Second", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Third", 300m)));

        await service.VoidAsync(second.Id, new VoidBillRequest("force failure"));

        var response = await service.PushSelectedAsync(new PushSelectedBillsRequest(
            [first.Id, second.Id, third.Id],
            null,
            "selected-stop"));

        Assert.Equal(3, response.Matched);
        Assert.Equal(1, response.Succeeded);
        Assert.Equal(1, response.Failed);
        Assert.True(response.StoppedOnFailure);
        Assert.Equal(second.Id, response.FailedBillId);
        Assert.Equal(new[] { first.Id, second.Id }, response.Items.Select(x => x.BillId).ToArray());

        var reloadedFirst = await service.GetAsync(first.Id);
        var reloadedThird = await service.GetAsync(third.Id);
        Assert.Equal(IBillService.StatePosted, reloadedFirst!.State);
        Assert.Equal(IBillService.StatePending, reloadedThird!.State);
    }

    [Fact]
    public async Task PushPending_StopsAtFirstFailure_AndLeavesLaterPendingBillsUntouched()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Second", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Third", 300m)));

        var broken = await db.Bills.FirstAsync(x => x.Id == second.Id);
        broken.CurrentRevisionId = null;
        await db.SaveChangesAsync();

        var response = await service.PushPendingAsync(new PushPendingBillsRequest(null, "pending-stop", 10));

        Assert.Equal(3, response.Matched);
        Assert.Equal(1, response.Succeeded);
        Assert.Equal(1, response.Failed);
        Assert.True(response.StoppedOnFailure);
        Assert.Equal(second.Id, response.FailedBillId);

        var reloadedFirst = await service.GetAsync(first.Id);
        var reloadedThird = await service.GetAsync(third.Id);
        Assert.Equal(IBillService.StatePosted, reloadedFirst!.State);
        Assert.Equal(IBillService.StatePending, reloadedThird!.State);
    }

    [Fact]
    public async Task PushFailedOutcome_LandsBillInFailedState()
    {
        await using var db = CreateDbContext();
        var failingPoster = new FakeTallyPoster(TallyPostOutcome.Failed);
        var service = BuildService(db, failingPoster);

        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest(null, "manual"));

        Assert.Equal(IBillService.StateFailed, pushed.State);
        Assert.Equal(1, failingPoster.CallCount);
    }

    [Fact]
    public async Task Search_DefaultWorkflowSort_PinsPendingThenOrdersNumberedHistoryByInvoiceDescending()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var pending = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Pending", 100m)));
        var legacyDraft = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Legacy", 200m)));
        var postedEarlier = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Queued-1", 300m)));
        var postedLater = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Queued-2", 400m)));

        var pendingEntity = await db.Bills.FirstAsync(x => x.Id == pending.Id);
        pendingEntity.CreatedAtUtc = new DateTimeOffset(2026, 4, 21, 8, 0, 0, TimeSpan.Zero);

        var draftEntity = await db.Bills.FirstAsync(x => x.Id == legacyDraft.Id);
        draftEntity.State = "draft";
        draftEntity.CreatedAtUtc = new DateTimeOffset(2026, 4, 21, 9, 0, 0, TimeSpan.Zero);

        var earlierEntity = await db.Bills.FirstAsync(x => x.Id == postedEarlier.Id);
        earlierEntity.CreatedAtUtc = new DateTimeOffset(2026, 4, 21, 10, 0, 0, TimeSpan.Zero);

        var laterEntity = await db.Bills.FirstAsync(x => x.Id == postedLater.Id);
        laterEntity.CreatedAtUtc = new DateTimeOffset(2026, 4, 21, 11, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();

        _ = await service.PushAsync(postedEarlier.Id, new PushBillRequest("2026-27", null));
        _ = await service.PushAsync(postedLater.Id, new PushBillRequest("2026-27", null));

        var response = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));

        Assert.Equal(
            new[] { pending.Id, legacyDraft.Id, postedLater.Id, postedEarlier.Id },
            response.Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Search_DefaultWorkflowSort_OrdersUnpaddedLegacyInvoiceNumbersNaturally()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var bill9 = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Nine", 100m)));
        var bill48 = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Forty eight", 200m)));
        var bill10 = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("Ten", 300m)));

        var rows = await db.Bills
            .Where(x => x.Id == bill9.Id || x.Id == bill48.Id || x.Id == bill10.Id)
            .ToListAsync();
        foreach (var row in rows)
        {
            row.State = IBillService.StatePosted;
            row.FiscalYear = "2026-27";
        }

        rows.Single(x => x.Id == bill9.Id).InvoiceNumber = "DDAJR/26-27/9";
        rows.Single(x => x.Id == bill48.Id).InvoiceNumber = "DDAJR/26-27/48";
        rows.Single(x => x.Id == bill10.Id).InvoiceNumber = "DDAJR/26-27/10";
        await db.SaveChangesAsync();

        var response = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));

        Assert.Equal(
            new[] { bill48.Id, bill10.Id, bill9.Id },
            response.Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Push_WritesAuditWithInvoiceNumber()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var submitted = await service.PushAsync(bill.Id, new PushBillRequest(null, "sub-audit"));

        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.push.requested");
        Assert.Contains(audits, a => a.EventType == "bill.push.requested" && a.PayloadJson.Contains(submitted.InvoiceNumber!));
    }

    // ---------- Change Invoice Number ----------

    [Fact]
    public async Task ChangeInvoiceNumber_PendingBill_UpdatesNumberAndWritesAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var oldNumber = bill.InvoiceNumber!;
        var newNumber = oldNumber.Replace("/0001", "/9999");

        var response = await service.ChangeInvoiceNumberAsync(bill.Id,
            new ChangeBillNumberRequest("9999", "fix", DryRun: false));

        Assert.True(response.Committed);
        Assert.Equal(newNumber, response.NewInvoiceNumber);
        Assert.True(response.LeavesGap);
        Assert.False(response.TallyDiverges);

        var reloaded = await service.GetAsync(bill.Id);
        Assert.Equal(newNumber, reloaded!.InvoiceNumber);

        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.number.changed" && a.PayloadJson.Contains(newNumber));
    }

    [Fact]
    public async Task ChangeInvoiceNumber_DryRun_DoesNotMutate()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var newNumber = bill.InvoiceNumber!.Replace("/0001", "/0007");

        var response = await service.ChangeInvoiceNumberAsync(bill.Id,
            new ChangeBillNumberRequest("7", null, DryRun: true));

        Assert.False(response.Committed);
        Assert.True(response.LeavesGap);
        var reloaded = await service.GetAsync(bill.Id);
        Assert.Equal(bill.InvoiceNumber, reloaded!.InvoiceNumber); // unchanged
    }

    [Fact]
    public async Task ChangeInvoiceNumber_PostedBill_SucceedsWithTallyDivergesFlag()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest(null, "s"));
        // simulate tally posted
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = IBillService.StatePosted;
        await db.SaveChangesAsync();

        var newNumber = bill.InvoiceNumber!.Replace("/0001", "/0050");
        var response = await service.ChangeInvoiceNumberAsync(bill.Id,
            new ChangeBillNumberRequest("50", "late correction", DryRun: false));

        Assert.True(response.Committed);
        Assert.True(response.TallyDiverges);
    }

    [Fact]
    public async Task ChangeInvoiceNumber_ConflictWithOtherBill_Throws()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var a = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var b = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("B", 200m)));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.ChangeInvoiceNumberAsync(a.Id, new ChangeBillNumberRequest("2", null, DryRun: false)));
    }

    [Fact]
    public async Task ChangeInvoiceNumber_PostingBill_Rejected()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = IBillService.StatePosting;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.ChangeInvoiceNumberAsync(bill.Id, new ChangeBillNumberRequest("1", null, DryRun: false)));
    }

    // ---------- Mark Posted / Pending ----------

    [Fact]
    public async Task MarkPosted_FromPending_TransitionsAndWritesAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        var response = await service.MarkPostedAsync(bill.Id,
            new MarkBillStateRequest("cash register offline reprint"));

        Assert.Equal(IBillService.StatePosted, response.State);
        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.mark_posted");
    }

    [Fact]
    public async Task MarkPosted_NullReason_Accepted()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        var response = await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest(null));

        Assert.Equal(IBillService.StatePosted, response.State);
    }

    [Fact]
    public async Task MarkPending_FromPosted_ClearsEditedAfterPushAndWritesAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = IBillService.StatePosted;
        entity.EditedAfterPush = true;
        await db.SaveChangesAsync();

        var response = await service.MarkPendingAsync(bill.Id,
            new MarkBillStateRequest("actually never made it to tally"));

        Assert.Equal(IBillService.StatePending, response.State);
        Assert.False(response.EditedAfterPush);
        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.mark_pending");
    }

    // ---------- Delete ----------

    [Fact]
    public async Task Delete_Pending_RemovesBillAndRevisions_KeepsAuditTombstone()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));

        var response = await service.DeleteAsync(bill.Id,
            new DeleteBillRequest("user cancelled", DryRun: false));

        Assert.True(response.Committed);
        Assert.False(response.TallyDiverges);
        Assert.Null(await db.Bills.FirstOrDefaultAsync(b => b.Id == bill.Id));
        Assert.Empty(await db.BillRevisions.Where(r => r.BillId == bill.Id).ToListAsync());
        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.deleted");
    }

    [Fact]
    public async Task Delete_Posted_SucceedsWithTallyDivergesFlag()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = IBillService.StatePosted;
        await db.SaveChangesAsync();

        var response = await service.DeleteAsync(bill.Id,
            new DeleteBillRequest("tally also voided manually", DryRun: false));

        Assert.True(response.Committed);
        Assert.True(response.TallyDiverges);
        Assert.Null(await db.Bills.FirstOrDefaultAsync(b => b.Id == bill.Id));
    }

    [Fact]
    public async Task Delete_DryRun_ReturnsFlagsWithoutMutating()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = IBillService.StatePosted;
        await db.SaveChangesAsync();

        var response = await service.DeleteAsync(bill.Id,
            new DeleteBillRequest(null, DryRun: true));

        Assert.False(response.Committed);
        Assert.True(response.TallyDiverges);
        Assert.NotNull(await db.Bills.FirstOrDefaultAsync(b => b.Id == bill.Id));
    }

    [Fact]
    public async Task Delete_Posting_Rejected()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = IBillService.StatePosting;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.DeleteAsync(bill.Id, new DeleteBillRequest(null, DryRun: false)));
    }

    [Fact]
    public async Task DeleteSelected_MixedStates_ReportsEachOutcome()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var pending = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("A", 100m)));
        var posted = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("B", 200m)));
        var postedEntity = await db.Bills.FirstAsync(b => b.Id == posted.Id);
        postedEntity.State = IBillService.StatePosted;
        var posting = await service.CreateDraftAsync(new CreateBillDraftRequest(null, SamplePayload("C", 300m)));
        var postingEntity = await db.Bills.FirstAsync(b => b.Id == posting.Id);
        postingEntity.State = IBillService.StatePosting;
        await db.SaveChangesAsync();

        var response = await service.DeleteSelectedAsync(new DeleteSelectedBillsRequest(
            new[] { pending.Id, posted.Id, posting.Id },
            "batch delete"));

        Assert.Equal(3, response.Requested);
        Assert.Equal(2, response.Deleted);
        Assert.Equal(1, response.Skipped);
        var failedItem = response.Items.Single(x => !x.Deleted);
        Assert.Equal(posting.Id, failedItem.BillId);
    }

    private static BillPayloadDto SamplePayload(string party, decimal grandTotal) =>
        new(
            PartyName: party,
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", grandTotal / 10m, grandTotal, "22K", null)
            ],
            Totals: new BillTotalsDto(grandTotal, 0m, 0m, 0m, grandTotal),
            Notes: null);

    private static BillService BuildService(ShowroomBillingDbContext db, ITallyPoster? poster = null)
    {
        var numbering = new NumberingService(db);
        return new BillService(db, numbering, poster ?? new FakeTallyPoster());
    }

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }

    internal sealed class FakeTallyPoster(TallyPostOutcome outcome = TallyPostOutcome.Posted) : ITallyPoster
    {
        public int CallCount { get; private set; }
        public TallyPostRequest? LastRequest { get; private set; }

        public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            var response = outcome == TallyPostOutcome.Posted
                ? new TallyPostResponse(TallyPostOutcome.Posted, "FAKE-VCH-1", null, null, "voucher-import-v1", null, null)
                : new TallyPostResponse(TallyPostOutcome.Failed, null, "FAKE_ERROR", "fake failure", "voucher-import-v1", null, null);
            return Task.FromResult(response);
        }
    }
}
