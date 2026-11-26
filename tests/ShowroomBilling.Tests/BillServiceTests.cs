using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Tally;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class BillServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task GetMany_MoreThanTheLimit_IsRejectedInsteadOfSilentlyCutShort()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();

        var ex = await Assert.ThrowsAsync<RequestValidationException>(
            () => service.GetManyAsync(new BillBatchGetRequest(ids)));

        Assert.Equal("At most 200 bills can be fetched at once (got 201).", ex.Message);
    }

    [Fact]
    public async Task Search_OrdersByTheNumericInvoiceCore_WhateverThePaddingOrSuffix()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var nine = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var fortyEight = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        var sevenWithSuffix = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));
        await RenumberAsync(db, nine.Id, "SR/9", 9);
        await RenumberAsync(db, fortyEight.Id, "SR/48", 48);
        // The longest text, but the lowest number: a text sort put it first.
        await RenumberAsync(db, sevenWithSuffix.Id, "SR/7-A", 7);

        var response = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));

        Assert.Equal(["SR/48", "SR/9", "SR/7-A"], response.Items.Select(item => item.InvoiceNumber));
    }

    private static async Task RenumberAsync(ShowroomBillingDbContext db, Guid billId, string invoiceNumber, long core)
    {
        var bill = await db.Bills.SingleAsync(b => b.Id == billId);
        bill.InvoiceNumber = invoiceNumber;
        bill.InvoiceCore = core;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task CreateDraft_PersistsBillWithRevisionOneAndReservesSerial()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var response = await service.CreateDraftAsync(new CreateBillDraftRequest(
            Payload: SamplePayload("Walk-in Customer", grandTotal: 1500m)));

        Assert.Equal(BillStates.Pending, response.State);
        Assert.NotNull(response.CurrentRevision);
        Assert.Equal(1, response.CurrentRevision!.RevisionNo);
        Assert.Equal("Walk-in Customer", response.CurrentRevision.Payload.PartyName);
        Assert.False(string.IsNullOrWhiteSpace(response.InvoiceNumber));
        Assert.Contains("/0001", response.InvoiceNumber);
        Assert.False(string.IsNullOrWhiteSpace(response.FiscalYear));

        var bill = Assert.Single(await db.Bills.ToListAsync());
        var revision = Assert.Single(await db.BillRevisions.ToListAsync());

        AssertVersion7(bill.Id);
        AssertVersion7(revision.Id);
        Assert.All(await db.AuditEvents.ToListAsync(), audit => AssertVersion7(audit.Id));
    }

    [Fact]
    public async Task CreateDraft_ReplayedSaveWithSameOperationId_ReturnsTheFirstBill()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var operationId = Guid.NewGuid();
        var request = new CreateBillDraftRequest(SamplePayload("A", 100m), operationId);

        var first = await service.CreateDraftAsync(request);
        var replay = await service.CreateDraftAsync(request);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.InvoiceNumber, replay.InvoiceNumber);
        Assert.Single(await db.Bills.ToListAsync());
        Assert.Equal(2L, await db.InvoiceSequences.Select(x => x.NextValue).SingleAsync());
    }

    [Fact]
    public async Task CreateDraft_ReusedOperationIdWithDifferentContents_IsRejected()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var operationId = Guid.NewGuid();
        await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m), operationId));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 250m), operationId)));
        Assert.Single(await db.Bills.ToListAsync());
    }

    [Fact]
    public async Task CreateDraft_WithoutOperationId_StillCreatesSeparateBills()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var request = new CreateBillDraftRequest(SamplePayload("A", 100m));

        await service.CreateDraftAsync(request);
        await service.CreateDraftAsync(request);

        Assert.Equal(2, await db.Bills.CountAsync());
    }

    [Fact]
    public async Task CreateDraft_AssignsConsecutiveSerialsAcrossDrafts()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 300m)));

        Assert.Contains("/0001", first.InvoiceNumber);
        Assert.Contains("/0002", second.InvoiceNumber);
        Assert.Contains("/0003", third.InvoiceNumber);
        Assert.Equal(4L, await db.InvoiceSequences.Select(x => x.NextValue).SingleAsync());
    }

    [Fact]
    public async Task CreateDraft_NumbersBillInItsBillDateFiscalYear()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var lastYearsBill = SamplePayload("A", 100m) with { BillDate = new DateOnly(2026, 3, 31) };
        var thisYearsBill = SamplePayload("B", 100m) with { BillDate = new DateOnly(2026, 4, 1) };

        var march = await service.CreateDraftAsync(new CreateBillDraftRequest(lastYearsBill));
        var april = await service.CreateDraftAsync(new CreateBillDraftRequest(thisYearsBill));

        Assert.Equal("2025-26", march.FiscalYear);
        Assert.Equal("2025-26/0001", march.InvoiceNumber);
        Assert.Equal("2026-27", april.FiscalYear);
        Assert.Equal("2026-27/0001", april.InvoiceNumber);
    }

    [Fact]
    public async Task UpdateDraft_RejectsBillDateInAnotherFiscalYear()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(
            SamplePayload("A", 100m) with { BillDate = new DateOnly(2026, 4, 1) }));

        await Assert.ThrowsAsync<BillStateConflictException>(() => service.UpdateDraftAsync(
            created.Id,
            new UpdateBillDraftRequest(SamplePayload("A", 100m) with { BillDate = new DateOnly(2026, 3, 31) })));
    }

    [Fact]
    public async Task UpdateDraft_StaleExpectedRevision_IsRejected()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var openedRevision = created.CurrentRevisionId;
        await service.UpdateDraftAsync(created.Id, new UpdateBillDraftRequest(SamplePayload("Counter 2 edit", 150m), openedRevision));

        await Assert.ThrowsAsync<BillStateConflictException>(() => service.UpdateDraftAsync(
            created.Id,
            new UpdateBillDraftRequest(SamplePayload("Counter 1 edit", 120m), openedRevision)));
        Assert.Equal("Counter 2 edit", (await service.GetAsync(created.Id))!.CurrentRevision!.Payload.PartyName);
    }

    [Fact]
    public async Task UpdateDraft_ReplayOfSaveThatAlreadyLanded_ReturnsStoredRevision()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var request = new UpdateBillDraftRequest(SamplePayload("A-edited", 150m), created.CurrentRevisionId);
        var first = await service.UpdateDraftAsync(created.Id, request);

        var replay = await service.UpdateDraftAsync(created.Id, request);

        Assert.Equal(first.CurrentRevisionId, replay.CurrentRevisionId);
        Assert.Equal(2, await db.BillRevisions.CountAsync());
    }

    [Fact]
    public async Task UpdateDraft_KeepsInvoiceNumberUnchanged()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var updated = await service.UpdateDraftAsync(created.Id, new UpdateBillDraftRequest(SamplePayload("A-updated", 150m)));

        Assert.Equal(created.InvoiceNumber, updated.InvoiceNumber);
        Assert.Equal(created.FiscalYear, updated.FiscalYear);
        Assert.Equal(2L, await db.InvoiceSequences.Select(x => x.NextValue).SingleAsync());
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

        var response = await service.CreateDraftAsync(new CreateBillDraftRequest(payload));

        var line = response.CurrentRevision!.Payload.Lines.Single();
        Assert.Equal("diamond", line.ItemCategory);
        Assert.Equal("wastage", line.PricingMode);
        Assert.Equal(3500m, line.DiamondRate);
    }

    [Fact]
    public async Task CreateDraft_RejectsExcessiveLineCountBeforePersistence()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var payload = SamplePayload("A", 100m);
        var oversized = payload with { Lines = Enumerable.Repeat(payload.Lines[0], 501).ToArray() };

        await Assert.ThrowsAsync<BillValidationException>(() =>
            service.CreateDraftAsync(new CreateBillDraftRequest(oversized)));
        Assert.Empty(await db.Bills.ToListAsync());
    }

    [Fact]
    public async Task CreateDraft_RejectsMalformedLineJsonAndOversizedPartyName()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var payload = SamplePayload(new string('A', 257), 100m);
        payload = payload with
        {
            Lines = [payload.Lines[0] with { RawJson = "not-json" }]
        };

        var exception = await Assert.ThrowsAsync<BillValidationException>(() =>
            service.CreateDraftAsync(new CreateBillDraftRequest(payload)));
        Assert.Contains(exception.Errors, x => x.Contains("PartyName", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, x => x.Contains("RawJson", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateDraft_RejectsControlCharactersThatTallyXmlCannotCarry()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var payload = SamplePayload("Ram Kumar", 100m);

        var exception = await Assert.ThrowsAsync<BillValidationException>(() =>
            service.CreateDraftAsync(new CreateBillDraftRequest(payload)));
        Assert.Contains(exception.Errors, x => x.Contains("PartyName", StringComparison.Ordinal));
        Assert.Empty(await db.Bills.ToListAsync());
    }

    [Fact]
    public async Task CreateDraft_RejectsMoneyWithMoreThanTwoDecimals()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var payload = SamplePayload("A", 100m) with
        {
            Totals = new BillTotalsDto(100m, 0.555m, 0m, 0.555m, 100m)
        };

        var exception = await Assert.ThrowsAsync<BillValidationException>(() =>
            service.CreateDraftAsync(new CreateBillDraftRequest(payload)));
        Assert.Contains(exception.Errors, x => x.Contains("DiscountTotal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_DoesNotReserveAnAdditionalNumber()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var draft = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var pushed = await service.PushAsync(draft.Id, new PushBillRequest("manual"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(draft.InvoiceNumber, pushed.InvoiceNumber);
        Assert.Equal(draft.FiscalYear, pushed.FiscalYear);
        Assert.Equal(2L, await db.InvoiceSequences.Select(x => x.NextValue).SingleAsync());
    }

    [Fact]
    public async Task UpdateDraft_AppendsNewRevisionAndPointsCurrentToIt()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.VoidAsync(bill.Id, new VoidBillRequest("user cancelled"));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("C", 300m))));
    }

    [Fact]
    public async Task UpdateDraft_OnAVoidedBill_SaysItIsVoided_EvenWhenTheDateIsAlsoWrong()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(
            SamplePayload("A", 100m) with { BillDate = new DateOnly(2026, 4, 1) }));
        await service.VoidAsync(bill.Id, new VoidBillRequest("user cancelled"));

        var ex = await Assert.ThrowsAsync<BillStateConflictException>(() => service.UpdateDraftAsync(
            bill.Id,
            new UpdateBillDraftRequest(SamplePayload("A", 100m) with { BillDate = new DateOnly(2026, 3, 31) })));

        Assert.Contains(BillStates.Voided, ex.Message);
    }

    [Fact]
    public async Task UpdateDraft_OnPostedBill_ReopensAsPendingWithEditedAfterPush()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("sub-1"));

        var updated = await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        Assert.Equal(BillStates.Pending, updated.State);
        Assert.True(updated.EditedAfterPush);
        Assert.Equal(bill.InvoiceNumber, updated.InvoiceNumber); // invoice number immutable through reopen
    }

    [Fact]
    public async Task UpdateDraft_OnPostedBill_WithoutAdmin_IsRefusedAndLeavesTheBillPosted()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var posted = await service.PushAsync(bill.Id, new PushBillRequest("sub-1"));

        var ex = await Assert.ThrowsAsync<AdminSessionRequiredException>(() =>
            service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m))));

        Assert.Contains(bill.InvoiceNumber!, ex.Message);
        await using var check = CreateDbContext();
        var stored = await check.Bills.SingleAsync(x => x.Id == bill.Id);
        Assert.Equal(BillStates.Posted, stored.State);
        Assert.False(stored.EditedAfterPush);
        Assert.Equal(posted.CurrentRevisionId, stored.CurrentRevisionId);
    }

    [Fact]
    public async Task Push_FirstPushUsesTallyCreate()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        Assert.Equal(TallyPostOperation.Create, poster.LastRequest!.Operation);
        Assert.Null(poster.LastRequest.TargetTagName);
        Assert.Null(poster.LastRequest.TargetTagValue);
    }

    [Fact]
    public async Task Push_EditedPostedBillAltersPreviousTallyVoucher()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var lookup = new FakeVoucherLookup();
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(2, poster.CallCount);
        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.False(pushed.EditedAfterPush);
        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        Assert.Equal("MASTER ID", poster.LastRequest.TargetTagName);
        Assert.Equal("101", poster.LastRequest.TargetTagValue);
        // Read back after the first push, read before the alter was sent, and
        // read back after it.
        Assert.Equal(["101", "101", "101"], lookup.AskedById);
        Assert.Empty(lookup.Asked);
    }

    [Fact]
    public async Task Push_EditedPostedBill_NamesTheVoucherByTheDateItHasInTally()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        // Tally holds the voucher under another date than the bill now has
        // (the date was edited, or changed in Tally by hand).
        var dateInTally = SampleBillDate.AddDays(-3);
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup { VoucherDate = dateInTally });
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(dateInTally, poster.LastRequest!.TargetVoucherDate);
        Assert.Equal(SampleBillDate, poster.LastRequest.Payload.BillDate);
        var requested = await db.AuditEvents
            .Where(a => a.EntityId == bill.Id.ToString() && a.EventType == "bill.push.requested")
            .OrderBy(a => a.CreatedAtUtc)
            .ToListAsync();
        Assert.Contains($"\"voucherDateInTally\":\"{dateInTally:yyyy-MM-dd}\"", requested[^1].PayloadJson);
    }

    [Fact]
    public async Task Push_EditedPostedBill_VoucherGoneFromTally_FailsWithNothingSent()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var lookup = new FakeVoucherLookup();
        lookup.Gone.Add("101");
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        // Tally answers an Alter it cannot match by creating a second voucher,
        // so nothing may be sent for a voucher it no longer has.
        Assert.Equal(1, poster.CallCount);
        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.True(pushed.EditedAfterPush);
        var status = await service.GetPostingStatusAsync(bill.Id);
        Assert.Equal("TALLY_VOUCHER_NOT_FOUND", status!.LastErrorCode);
        Assert.Contains("101", status.LastErrorMessage);
    }

    [Fact]
    public async Task Push_EditedPostedBill_SettingsNowNameAnotherCompany_FailsWithNothingSent()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101") { Company = "Company A" };
        // A master id names a voucher only within its company: id 101 in
        // Company B is somebody else's sale.
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup { Company = "Company B" });
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        Assert.Contains(
            "\"company\":\"Company A\"",
            (await db.AuditEvents.SingleAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == "tally.posted")).PayloadJson);
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(1, poster.CallCount);
        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.True(pushed.EditedAfterPush);
        var status = await service.GetPostingStatusAsync(bill.Id);
        Assert.Equal("TALLY_COMPANY_CHANGED", status!.LastErrorCode);
        Assert.Contains("Company A", status.LastErrorMessage);
    }

    [Fact]
    public async Task Push_EditedBill_PushRecordedACompanyButNoId_OtherCompanyIsRefusedBeforeAnyRead()
    {
        await using var db = CreateDbContext();
        // Tally accepted the create without giving an id; the push still recorded its company.
        var poster = new FakeTallyPoster(remoteId: "post-key", tallyMasterId: null) { Company = "Company A" };
        var lookup = new FakeVoucherLookup("4521") { Company = "Company B", VoucherAmount = 100m };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        // Company B's voucher with the number is somebody else's sale; it is not even looked for.
        Assert.Empty(lookup.Asked);
        Assert.Empty(lookup.AskedById);
        Assert.Equal(1, poster.CallCount);
        Assert.Equal("TALLY_COMPANY_CHANGED", (await service.GetPostingStatusAsync(bill.Id))!.LastErrorCode);
        Assert.Equal(BillStates.Failed, pushed.State);
    }

    [Theory]
    [InlineData(100, true)]   // the voucher shows the bill as pushed
    [InlineData(999, false)]  // another company's sale with the same id, or the voucher changed in Tally
    public async Task Push_EditedBill_PushRecordedBeforePushesNamedTheirCompany_IdIsHeldToTheBill(
        int amountInTally,
        bool alters)
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101") { Company = null };
        var lookup = new FakeVoucherLookup { VoucherAmount = amountInTally };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        // The first push's read-back, the read before the alter, and the alter's read-back.
        Assert.Equal(alters ? ["101", "101", "101"] : ["101", "101"], lookup.AskedById);
        if (alters)
        {
            Assert.Equal(BillStates.Posted, pushed.State);
            Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        }
        else
        {
            Assert.Equal(1, poster.CallCount);
            Assert.Equal(BillStates.Failed, pushed.State);
            Assert.Equal("TALLY_VOUCHER_MISMATCH", (await service.GetPostingStatusAsync(bill.Id))!.LastErrorCode);
        }
    }

    [Fact]
    public async Task Push_EditedBill_AfterMarkAsPendingTheOldPushNoLongerNamesTheVoucher()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        // The pushed voucher was deleted in Tally and its replacement entered by hand.
        var lookup = new FakeVoucherLookup("4521") { VoucherAmount = 100m };
        lookup.Gone.Add("101");
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.MarkPendingAsync(bill.Id, new MarkBillStateRequest("voucher deleted in Tally"));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("re-entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("push-edited"));

        // Only the first push's read-back asked for 101; the alter target was found by number.
        Assert.Equal(["101", "4521"], lookup.AskedById);
        Assert.Equal([(bill.InvoiceNumber!, bill.FiscalYear!)], lookup.Asked);
        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal("4521", poster.LastRequest!.TargetTagValue);
    }

    [Fact]
    public async Task Push_EditedBill_ANumericRemoteIdThatTallyDidNotGiveIsNotTakenForAMasterId()
    {
        await using var db = CreateDbContext();
        // Tally accepted the voucher without an id, and the key it was sent with is all digits.
        var poster = new FakeTallyPoster(remoteId: "101", tallyMasterId: null);
        var lookup = new FakeVoucherLookup();
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Empty(lookup.AskedById);
        Assert.Single(lookup.Asked);
        Assert.Equal(1, poster.CallCount);
    }

    [Fact]
    public async Task Push_EditedPostedBill_TheAlterGoesWhereItsVoucherWasRead()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(TestCompany, poster.LastRequest!.TargetCompany);
        Assert.Equal("Sales", poster.LastRequest.TargetVoucherType);
    }

    [Fact]
    public async Task Push_EditedPostedBill_SameCompanyWrittenOtherwise_Alters()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101") { Company = "Company A" };
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup { Company = "company a" });
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
    }

    [Theory]
    [InlineData(0, 999)]   // same date, another total
    [InlineData(-1, 100)]  // same total, another date
    [InlineData(0, null)]  // Tally gave no total to check
    public async Task Push_BillMarkedPushedByHandThenEdited_VoucherWithTheNumberIsAnotherSale_FailsWithNothingSent(
        int daysOff,
        int? amountInTally)
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        // With Auto Renumber the one voucher now carrying the number can be a different sale.
        var lookup = new FakeVoucherLookup("4521")
        {
            VoucherDate = SampleBillDate.AddDays(daysOff),
            VoucherAmount = amountInTally,
        };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("push-edited"));

        Assert.Equal(0, poster.CallCount);
        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.True(pushed.EditedAfterPush);
        var status = await service.GetPostingStatusAsync(bill.Id);
        Assert.Equal("TALLY_VOUCHER_MISMATCH", status!.LastErrorCode);
        Assert.Contains(bill.InvoiceNumber!, status.LastErrorMessage);
    }

    [Fact]
    public async Task Push_EditedPostedBill_TallyCannotBeAskedForTheVoucher_LeavesTheBillAsItWas()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var lookup = new FakeVoucherLookup { Failure = new HttpRequestException("No connection could be made") };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var ex = await Assert.ThrowsAsync<TallyPreflightUnavailableException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest("repush-edited")));

        Assert.Contains("101", ex.Message);
        Assert.Equal(1, poster.CallCount);
        await using var check = CreateDbContext();
        var stored = await check.Bills.SingleAsync(x => x.Id == bill.Id);
        Assert.Equal(BillStates.Pending, stored.State);
        Assert.True(stored.EditedAfterPush);
    }

    [Fact]
    public async Task Repost_UneditedPostedBillStillUsesTallyCreate()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        await service.RepostAsync(bill.Id, new RepostBillRequest("repost-1", "plain-repost"));

        Assert.Equal(2, poster.CallCount);
        Assert.Equal(TallyPostOperation.Create, poster.LastRequest!.Operation);
        Assert.Equal("repost-1", poster.LastRequest.IdempotencyKey);
    }

    [Fact]
    public async Task Push_EditedPostedBillWithMissingTallyTargetFailsWithoutCallingTallyAgain()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(remoteId: "FAKE-VCH-1", tallyMasterId: null);
        // No recorded id and no way to ask Tally: there is nothing to name the voucher by.
        var service = BuildService(db, poster, withoutVoucherLookup: true);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(1, poster.CallCount);
        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.True(pushed.EditedAfterPush);
        var status = await service.GetPostingStatusAsync(bill.Id);
        Assert.Equal("TALLY_ALTER_TARGET_MISSING", status!.LastErrorCode);
    }

    [Fact]
    public async Task Push_BillMarkedPushedByHandThenEdited_AltersTheVoucherTallyHasUnderItsNumber()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        // Tally's voucher with the number shows the bill as it was marked: same date, same total.
        var lookup = new FakeVoucherLookup("4521") { VoucherAmount = 100m };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("push-edited"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.False(pushed.EditedAfterPush);
        Assert.Equal([(bill.InvoiceNumber!, bill.FiscalYear!)], lookup.Asked);
        Assert.Equal(1, poster.CallCount);
        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        Assert.Equal("MASTER ID", poster.LastRequest.TargetTagName);
        Assert.Equal("4521", poster.LastRequest.TargetTagValue);
        Assert.Equal(lookup.VoucherDate, poster.LastRequest.TargetVoucherDate);

        // The id is on record now: the next edit asks Tally for that voucher, not for the number.
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited-again", 160m)), callerIsAdmin: true);
        await service.PushAsync(bill.Id, new PushBillRequest("push-edited-again"));

        // Read back after the first alter, read before the second, read back after it.
        Assert.Single(lookup.Asked);
        Assert.Equal(["4521", "4521", "4521"], lookup.AskedById);
        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        Assert.Equal("4521", poster.LastRequest.TargetTagValue);
    }

    [Fact]
    public async Task Push_ReadsTheVoucherBackAndRecordsTheNumberTallyFiledItUnder()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        // With Automatic numbering Tally gives the voucher a number of its own.
        var lookup = new FakeVoucherLookup { VoucherNumber = "TALLY/2", VoucherAmount = 100m };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal("TALLY/2", pushed.TallyVoucherNumber);
        Assert.Equal(["101"], lookup.AskedById);
        await using var check = CreateDbContext();
        var filed = AuditDetails(await check.AuditEvents.SingleAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyVoucherFiled));
        var posted = await check.AuditEvents.SingleAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyPosted);
        Assert.Equal(posted.Id.ToString(), filed.GetProperty("pushAuditId").GetString());
        Assert.Equal("TALLY/2", filed.GetProperty("voucherNumberInTally").GetString());
        Assert.Equal("101", filed.GetProperty("tallyMasterId").GetString());
        Assert.True(filed.GetProperty("numberDiffers").GetBoolean());
        Assert.Equal(SampleBillDate.ToString("yyyy-MM-dd"), filed.GetProperty("voucherDateInTally").GetString());
        Assert.Equal(100m, filed.GetProperty("totalInTally").GetDecimal());
        Assert.Equal(bill.InvoiceNumber, filed.GetProperty("invoiceNumber").GetString());

        var listed = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));
        Assert.Equal("TALLY/2", Assert.Single(listed.Items).TallyVoucherNumber);
    }

    [Fact]
    public async Task Push_VoucherFiledUnderTheBillsOwnNumber_IsRecordedAsTheSame()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var lookup = new FakeVoucherLookup();
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        lookup.VoucherNumber = bill.InvoiceNumber;

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));

        Assert.Equal(bill.InvoiceNumber, pushed.TallyVoucherNumber);
        var filed = AuditDetails(await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyVoucherFiled));
        Assert.False(filed.GetProperty("numberDiffers").GetBoolean());
    }

    [Fact]
    public async Task Push_VoucherTallyShowsNoNumberFor_IsRecordedAsNotUnderTheBillsNumber()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        // VoucherNumber left null: Tally lists the voucher without a number.
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup());
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        var filed = AuditDetails(await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyVoucherFiled));
        Assert.True(filed.GetProperty("numberDiffers").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, filed.GetProperty("voucherNumberInTally").ValueKind);
    }

    [Fact]
    public async Task Push_VoucherCannotBeReadBack_ThePushStands()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var lookup = new FakeVoucherLookup { ByIdFailure = new HttpRequestException("No connection could be made") };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Null(pushed.TallyVoucherNumber);
        Assert.Equal(["101"], lookup.AskedById);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyVoucherFiled));
    }

    [Fact]
    public async Task Push_VoucherReadBackInAnotherCompany_IsNotUsed()
    {
        await using var db = CreateDbContext();
        // Settings named another company by the time the voucher was read back:
        // id 101 there is somebody else's sale.
        var poster = new FakeTallyPoster(tallyMasterId: "101") { Company = "Company A" };
        var lookup = new FakeVoucherLookup { Company = "Company B", VoucherNumber = "OTHER/1" };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Null(pushed.TallyVoucherNumber);
        Assert.False(await db.AuditEvents.AnyAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyVoucherFiled));
    }

    [Fact]
    public async Task Push_NoIdFromTally_NothingIsReadBack()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(remoteId: "post-key", tallyMasterId: null);
        var lookup = new FakeVoucherLookup { VoucherNumber = "TALLY/2" };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Null(pushed.TallyVoucherNumber);
        Assert.Empty(lookup.AskedById);
    }

    [Fact]
    public async Task List_NumberTallyFiledTheBillUnder_IsDroppedOnceMarkAsPendingSaysTallyNoLongerHoldsIt()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup { VoucherNumber = "TALLY/2" });
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        await service.MarkPendingAsync(bill.Id, new MarkBillStateRequest("voucher deleted in Tally"));

        var listed = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));
        Assert.Null(Assert.Single(listed.Items).TallyVoucherNumber);
    }

    [Fact]
    public async Task List_AReadBackCountsOnlyForThePushItBelongsTo()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(new[]
        {
            TallyPostResponse.Posted("first", tallyMasterId: "101"),
            TallyPostResponse.Posted("repost", tallyMasterId: "202"),
        });
        var lookup = new FakeVoucherLookup { VoucherNumber = "TALLY/2" };
        lookup.Gone.Add("202");
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        // The repost made voucher 202, which could not be read back.
        await service.RepostAsync(bill.Id, new RepostBillRequest("repost", "again"));
        // A read-back of the first push that finished late, after the repost settled.
        db.AuditEvents.Add(new ShowroomBilling.Infrastructure.Persistence.Entities.AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            EntityType = "bill",
            EntityId = bill.Id.ToString(),
            EventType = BillAuditEvents.TallyVoucherFiled,
            ActorType = "system",
            PayloadJson = "{\"state\":\"posted\",\"details\":{\"tallyMasterId\":\"101\",\"voucherNumberInTally\":\"TALLY/9\"}}",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
        });
        await db.SaveChangesAsync();

        var listed = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));

        Assert.Null(Assert.Single(listed.Items).TallyVoucherNumber);
    }

    [Fact]
    public async Task PushSelected_EachItemCarriesTheNumberTallyFiledItUnder()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup { VoucherNumber = "TALLY/2" });
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.PushSelectedAsync(new PushSelectedBillsRequest([bill.Id], "selected"));

        Assert.Equal("TALLY/2", Assert.Single(response.Items).TallyVoucherNumber);
    }

    [Fact]
    public async Task Push_ACreateTallyTookAsAnAlterOfAnExistingVoucher_IsRecordedAsSuch()
    {
        await using var db = CreateDbContext();
        // Tally already had a voucher with this REMOTEID: it updated that one.
        var poster = new FakeTallyPoster(new[]
        {
            TallyPostResponse.Posted("FAKE-VCH-1", tallyMasterId: "101", updatedExistingVoucher: true)
        });
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        var posted = AuditDetails(await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == BillAuditEvents.TallyPosted));
        Assert.True(posted.GetProperty("updatedExistingVoucher").GetBoolean());
    }

    [Fact]
    public async Task Push_BillMarkedPushedByHandThenEdited_NoSuchVoucherInTally_FailsWithNothingSent()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup());
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("push-edited"));

        Assert.Equal(0, poster.CallCount);
        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.True(pushed.EditedAfterPush);
        var status = await service.GetPostingStatusAsync(bill.Id);
        Assert.Equal("TALLY_VOUCHER_NOT_FOUND", status!.LastErrorCode);
        Assert.Contains(bill.InvoiceNumber!, status.LastErrorMessage);

        // The way out the message names: the admin says Tally does not hold
        // it, and the next push creates the voucher.
        await service.MarkPendingAsync(bill.Id, new MarkBillStateRequest("checked: not in Tally"));
        var created = await service.PushAsync(bill.Id, new PushBillRequest("push-as-new"));

        Assert.Equal(BillStates.Posted, created.State);
        Assert.Equal(TallyPostOperation.Create, poster.LastRequest!.Operation);
    }

    [Fact]
    public async Task Push_BillMarkedPushedByHandThenEdited_TwoVouchersWithTheNumber_FailsWithNothingSent()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster, voucherLookup: new FakeVoucherLookup("4521", "4777"));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("push-edited"));

        Assert.Equal(0, poster.CallCount);
        Assert.Equal(BillStates.Failed, pushed.State);
        var status = await service.GetPostingStatusAsync(bill.Id);
        Assert.Equal("TALLY_VOUCHER_AMBIGUOUS", status!.LastErrorCode);
    }

    [Fact]
    public async Task Push_BillMarkedPushedByHandThenEdited_TallyCannotBeAsked_LeavesTheBillAsItWas()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var lookup = new FakeVoucherLookup { Failure = new HttpRequestException("No connection could be made") };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var ex = await Assert.ThrowsAsync<TallyPreflightUnavailableException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest("push-edited")));

        Assert.Contains(bill.InvoiceNumber!, ex.Message);
        Assert.Equal(0, poster.CallCount);
        await using var check = CreateDbContext();
        var stored = await check.Bills.SingleAsync(x => x.Id == bill.Id);
        Assert.Equal(BillStates.Pending, stored.State);
        Assert.True(stored.EditedAfterPush);
    }

    [Fact]
    public async Task Push_BillMarkedPushedByHandThenRenumbered_LooksForTheNumberTallyStillHas()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var lookup = new FakeVoucherLookup("4521") { VoucherAmount = 100m };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        var renamed = await service.ChangeInvoiceNumberAsync(bill.Id, new ChangeBillNumberRequest("7", "typo", DryRun: false));

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("after-rename"));

        // Tally has the voucher under the old number; the Alter carries the new one.
        Assert.Equal([(bill.InvoiceNumber!, bill.FiscalYear!)], lookup.Asked);
        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        Assert.Equal("4521", poster.LastRequest.TargetTagValue);
        Assert.Equal(renamed.NewInvoiceNumber, poster.LastRequest.InvoiceNumber);
    }

    [Fact]
    public async Task Push_FailedAlterKeepsEditFlagAndPreEditAudit()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(new[]
        {
            PostedResponse(remoteId: "101", tallyMasterId: "101"),
            FailedResponse("TALLY_NO_EFFECT", "No alteration.")
        });
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.True(pushed.EditedAfterPush);
        Assert.Equal(1, await db.AuditEvents.CountAsync(a => a.EntityId == bill.Id.ToString() && a.EventType == "tally.posted"));
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == "tally.failed" && a.PayloadJson.Contains("TALLY_NO_EFFECT"));
    }

    [Fact]
    public async Task Push_SuccessfulAlterClearsEditFlagAndPreservesAuditHistory()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("repush-edited"));

        Assert.False(pushed.EditedAfterPush);
        var postedAudits = await db.AuditEvents
            .Where(a => a.EntityId == bill.Id.ToString() && a.EventType == "tally.posted")
            .ToListAsync();
        Assert.Equal(2, postedAudits.Count);
        Assert.Contains("\"tallyAction\":\"Alter\"", postedAudits[^1].PayloadJson);
        var trail = await service.GetAuditAsync(bill.Id);
        // The alter is read back from Tally once it is saved.
        Assert.Equal(
            ["bill.edit.reopened", "bill.push.requested", "tally.posted", "tally.voucher.filed"],
            trail!.Events.TakeLast(4).Select(x => x.EventType).ToArray());
        Assert.Contains("\"tallyMasterId\":\"101\"", postedAudits[^1].PayloadJson);
    }

    [Fact]
    public async Task Push_CarriesDraftReservedNumberAndTransitionsToPosted()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var submitted = await service.PushAsync(bill.Id, new PushBillRequest("sub-1"));

        Assert.Equal(BillStates.Posted, submitted.State);
        Assert.Equal(bill.InvoiceNumber, submitted.InvoiceNumber);
        Assert.Equal(bill.FiscalYear, submitted.FiscalYear);
        Assert.Contains("/0001", submitted.InvoiceNumber);
    }

    [Fact]
    public async Task Push_OnAlreadyPostedBill_Rejects()
    {
        // Sync push: once a bill is posted, it's a terminal state for normal push.
        // Reposting is a separate workflow (Repost), not a second Submit.
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest("second")));
    }

    [Fact]
    public async Task Push_RejectsAlreadyVoidedBill()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.VoidAsync(bill.Id, new VoidBillRequest("mistake"));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest("x")));
    }

    [Fact]
    public async Task Revise_ReservesANumberSoTheRevisedBillCanBePushed()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster);
        var original = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var revised = await service.ReviseAsync(original.Id, new ReviseBillRequest(SamplePayload("A-revised", 120m)));
        var pushed = await service.PushAsync(revised.Id, new PushBillRequest("push revised"));

        Assert.EndsWith("/0002", revised.InvoiceNumber);
        Assert.Equal(original.FiscalYear, revised.FiscalYear);
        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(revised.InvoiceNumber, poster.LastRequest!.InvoiceNumber);
        Assert.Equal(3L, await db.InvoiceSequences.Select(x => x.NextValue).SingleAsync());
    }

    [Fact]
    public async Task Revise_FromDraft_CreatesNewDraftAndMarksPriorAsRevised()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var prior = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var revised = await service.ReviseAsync(prior.Id, new ReviseBillRequest(InitialPayload: null));

        Assert.NotEqual(prior.Id, revised.Id);
        Assert.Equal(BillStates.Pending, revised.State);
        Assert.Equal(1, revised.CurrentRevision!.RevisionNo);

        var reloadedPrior = await service.GetAsync(prior.Id);
        Assert.Equal(BillStates.Revised, reloadedPrior!.State);
        Assert.Equal(revised.Id, reloadedPrior.SupersededByBillId);
    }

    [Fact]
    public async Task Void_FromDraft_TransitionsToVoidedAndSetsVoidedAt()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var voided = await service.VoidAsync(bill.Id, new VoidBillRequest("customer cancelled"));

        Assert.Equal(BillStates.Voided, voided.State);
    }

    [Fact]
    public async Task Void_RejectsTerminalStates()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.VoidAsync(bill.Id, new VoidBillRequest(null));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.VoidAsync(bill.Id, new VoidBillRequest(null)));
    }

    [Fact]
    public async Task Search_FiltersByStateAndPaginates()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var d1 = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var d2 = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 200m)));
        await service.PushAsync(d2.Id, new PushBillRequest("s2"));
        _ = d1;

        var drafts = await service.SearchAsync(new BillSearchFilter(BillStates.Pending, null, null, null, null, null));
        var queued = await service.SearchAsync(new BillSearchFilter(BillStates.Posted, null, null, null, null, null));

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

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Second", 200m)));

        var response = await service.PushSelectedAsync(new PushSelectedBillsRequest(
            [second.Id, first.Id],
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

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Second", 200m)));
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

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Second", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Third", 300m)));

        await service.VoidAsync(second.Id, new VoidBillRequest("force failure"));

        var response = await service.PushSelectedAsync(new PushSelectedBillsRequest(
            [first.Id, second.Id, third.Id],
            "selected-stop"));

        Assert.Equal(3, response.Matched);
        Assert.Equal(1, response.Succeeded);
        Assert.Equal(1, response.Failed);
        Assert.True(response.StoppedOnFailure);
        Assert.Equal(second.Id, response.FailedBillId);
        Assert.Equal(new[] { first.Id, second.Id }, response.Items.Select(x => x.BillId).ToArray());

        var reloadedFirst = await service.GetAsync(first.Id);
        var reloadedThird = await service.GetAsync(third.Id);
        Assert.Equal(BillStates.Posted, reloadedFirst!.State);
        Assert.Equal(BillStates.Pending, reloadedThird!.State);
    }

    [Fact]
    public async Task PushPending_StopsAtFirstFailure_AndLeavesLaterPendingBillsUntouched()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(
        [
            TallyPostResponse.Posted("FAKE-VCH-1"),
            TallyPostResponse.Failed("LINEERROR", "Ledger missing."),
        ]));

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Second", 200m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Third", 300m)));

        var response = await service.PushPendingAsync(new PushPendingBillsRequest("pending-stop", 10));

        Assert.Equal(3, response.Matched);
        Assert.Equal(1, response.Succeeded);
        Assert.Equal(1, response.Failed);
        Assert.True(response.StoppedOnFailure);
        Assert.Equal(second.Id, response.FailedBillId);

        var reloadedFirst = await service.GetAsync(first.Id);
        var reloadedThird = await service.GetAsync(third.Id);
        Assert.Equal(BillStates.Posted, reloadedFirst!.State);
        Assert.Equal(BillStates.Pending, reloadedThird!.State);
    }

    [Fact]
    public async Task PushPending_GoesByBillDate_ThenNumber_NotByWhenTheBillWasSaved()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster);
        var day = new DateOnly(2026, 10, 11);
        // Saved first, but dated a day later: Tally files it after the other two.
        var later = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Later", 100m) with { BillDate = day.AddDays(1) }));
        var a = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m) with { BillDate = day }));
        var b = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m) with { BillDate = day }));
        // A renumbered above B since it was saved.
        await db.Bills.Where(x => x.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.InvoiceCore, (long?)10));

        var response = await service.PushPendingAsync(new PushPendingBillsRequest("in order", 10));

        Assert.Equal(3, response.Succeeded);
        Assert.Equal([b.Id, a.Id, later.Id], poster.Requests.Select(r => r.BillId));
    }

    [Fact]
    public async Task PushFailedOutcome_LandsBillInFailedState()
    {
        await using var db = CreateDbContext();
        var failingPoster = new FakeTallyPoster(TallyPostOutcome.Failed);
        var service = BuildService(db, failingPoster);

        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("manual"));

        Assert.Equal(BillStates.Failed, pushed.State);
        Assert.Equal(1, failingPoster.CallCount);
    }

    [Fact]
    public async Task PushUnknownOutcome_RequiresManualReconciliationAndCannotRetry()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(TallyPostOutcome.Unknown);
        var service = BuildService(db, poster);

        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("manual"));

        Assert.Equal(BillStates.ReconciliationRequired, pushed.State);
        Assert.Equal(1, poster.CallCount);
        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.RetryAsync(bill.Id, new RetryBillPostingRequest("unsafe retry")));
        Assert.Contains(
            await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync(),
            x => x.EventType == "tally.outcome.unknown");
    }

    [Fact]
    public async Task ReconciliationRequired_CanBeResolvedToPostedOrPendingByAdmin()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(TallyPostOutcome.Unknown));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("manual"));

        var posted = await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("verified in Tally"));
        Assert.Equal(BillStates.Posted, posted.State);

        var pending = await service.MarkPendingAsync(bill.Id, new MarkBillStateRequest("confirmed absent in Tally"));
        Assert.Equal(BillStates.Pending, pending.State);
    }

    [Fact]
    public async Task PushSelected_StopsWhenTallyReturnsFailureAndDoesNotCountItAsSuccess()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(TallyPostOutcome.Failed);
        var service = BuildService(db, poster);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Second", 200m)));

        var response = await service.PushSelectedAsync(
            new PushSelectedBillsRequest([first.Id, second.Id], "selected-stop"));

        Assert.Equal(0, response.Succeeded);
        Assert.Equal(1, response.Failed);
        Assert.True(response.StoppedOnFailure);
        Assert.Equal(first.Id, response.FailedBillId);
        Assert.Equal(1, poster.CallCount);
        Assert.Equal(BillStates.Pending, (await service.GetAsync(second.Id))!.State);
    }

    [Fact]
    public async Task Push_WhenTallyPreflightIsUnhealthy_LeavesBillPendingAndDoesNotCallTally()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster, FakeTallyHealth.Unhealthy("Tally is unreachable."));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var ex = await Assert.ThrowsAsync<TallyPreflightUnavailableException>(() =>
            service.PushAsync(bill.Id, new PushBillRequest("manual")));

        Assert.Contains("Tally push blocked", ex.Message);
        Assert.Equal(0, poster.CallCount);
        var reloaded = await service.GetAsync(bill.Id);
        Assert.Equal(BillStates.Pending, reloaded!.State);
        Assert.DoesNotContain(await db.AuditEvents.ToListAsync(), a => a.EventType == "bill.push.requested");
    }

    [Fact]
    public async Task Retry_WhenTallyPreflightIsUnhealthy_LeavesBillFailedAndDoesNotCallTally()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster, FakeTallyHealth.Unhealthy("Tally is unreachable."));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(x => x.Id == bill.Id);
        entity.State = BillStates.Failed;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<TallyPreflightUnavailableException>(() =>
            service.RetryAsync(bill.Id, new RetryBillPostingRequest("retry")));

        Assert.Equal(0, poster.CallCount);
        var reloaded = await service.GetAsync(bill.Id);
        Assert.Equal(BillStates.Failed, reloaded!.State);
    }

    [Fact]
    public async Task Repost_WhenTallyPreflightIsUnhealthy_LeavesPostedBillPostedAndDoesNotCallTally()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster, FakeTallyHealth.Unhealthy("Tally is unreachable."));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(x => x.Id == bill.Id);
        entity.State = BillStates.Posted;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<TallyPreflightUnavailableException>(() =>
            service.RepostAsync(bill.Id, new RepostBillRequest(Guid.NewGuid().ToString("N"), "repost")));

        Assert.Equal(0, poster.CallCount);
        var reloaded = await service.GetAsync(bill.Id);
        Assert.Equal(BillStates.Posted, reloaded!.State);
    }

    [Fact]
    public async Task PushPending_WhenTallyPreflightIsUnhealthy_LeavesAllPendingAndDoesNotCallTally()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster();
        var service = BuildService(db, poster, FakeTallyHealth.Unhealthy("Tally is unreachable."));

        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("First", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Second", 200m)));

        await Assert.ThrowsAsync<TallyPreflightUnavailableException>(() =>
            service.PushPendingAsync(new PushPendingBillsRequest("push all", 10)));

        Assert.Equal(0, poster.CallCount);
        Assert.Equal(BillStates.Pending, (await service.GetAsync(first.Id))!.State);
        Assert.Equal(BillStates.Pending, (await service.GetAsync(second.Id))!.State);
    }

    [Fact]
    public async Task Search_DefaultWorkflowSort_PinsPendingThenOrdersNumberedHistoryByInvoiceDescending()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var pending = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Pending", 100m)));
        var legacyDraft = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Legacy", 200m)));
        var postedEarlier = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Queued-1", 300m)));
        var postedLater = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Queued-2", 400m)));

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

        _ = await service.PushAsync(postedEarlier.Id, new PushBillRequest(null));
        _ = await service.PushAsync(postedLater.Id, new PushBillRequest(null));

        var response = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null));

        // legacyDraft has /0002, pending has /0001 — workflow sort puts the
        // higher invoice number on top within the pending/draft group. The
        // posted group follows, also by invoice number desc.
        Assert.Equal(
            new[] { legacyDraft.Id, pending.Id, postedLater.Id, postedEarlier.Id },
            response.Items.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Search_DefaultWorkflowSort_OrdersUnpaddedLegacyInvoiceNumbersNaturally()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var bill9 = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Nine", 100m)));
        var bill48 = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Forty eight", 200m)));
        var bill10 = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Ten", 300m)));

        var rows = await db.Bills
            .Where(x => x.Id == bill9.Id || x.Id == bill48.Id || x.Id == bill10.Id)
            .ToListAsync();
        foreach (var row in rows)
        {
            row.State = BillStates.Posted;
            row.FiscalYear = "2026-27";
            // A legacy row has no stored core, so the sort falls back to the text.
            row.InvoiceCore = null;
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
    public async Task Search_FiltersByInvoiceNumberOrPartyName()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);

        var alpha = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Alpha Jewels", 100m)));
        var beta = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("Beta Customer", 200m)));

        var byParty = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null, Search: "Alpha"));
        var byInvoice = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null, Search: beta.InvoiceNumber![^4..]));

        Assert.Equal(alpha.Id, Assert.Single(byParty.Items).Id);
        Assert.Equal(beta.Id, Assert.Single(byInvoice.Items).Id);
    }

    [Fact]
    public async Task Push_WritesAuditWithInvoiceNumber()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var submitted = await service.PushAsync(bill.Id, new PushBillRequest("sub-audit"));

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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("s"));
        // simulate tally posted
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = BillStates.Posted;
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
        var a = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var b = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 200m)));

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.ChangeInvoiceNumberAsync(a.Id, new ChangeBillNumberRequest("2", null, DryRun: false)));
    }

    [Fact]
    public async Task ChangeInvoiceNumber_PostingBill_Rejected()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = BillStates.Posting;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.ChangeInvoiceNumberAsync(bill.Id, new ChangeBillNumberRequest("1", null, DryRun: false)));
    }

    [Fact]
    public async Task ChangeInvoiceNumber_MovesTrailingDown_RollsBackSequence()
    {
        // Repro: create three bills (1, 2, 3) so NextValue lands at 4. Rename
        // the trailing bill from /0003 to /0001-vacancy (any number strictly
        // below the max-of-remaining). The freed trailing core should be
        // reclaimed by the rollback so NextValue points back at 3, and the
        // next reservation reuses 3 instead of skipping to 4.
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));

        // Delete the original /0001 so /0001 becomes a free core; then rename
        // bill #3 (currently /0003) to /0001. After the rename, occupied cores
        // are {1, 2}; max = 2; NextValue should roll back to 3.
        await service.DeleteAsync(first.Id, new DeleteBillRequest(null, DryRun: false));

        var response = await service.ChangeInvoiceNumberAsync(third.Id,
            new ChangeBillNumberRequest("1", "reclaim trailing", DryRun: false));

        Assert.True(response.Committed);
        Assert.Equal(3L, response.SequenceNextValue);

        // The next reservation should now pick up the freed trailing core (3),
        // not skip ahead to 4.
        var next = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("D", 100m)));
        Assert.EndsWith("/0003", next.InvoiceNumber);
    }

    [Fact]
    public async Task ChangeInvoiceNumber_MovesNumberForward_DoesNotRegressSequence()
    {
        // Renaming a non-trailing bill upward (creating a forward gap) must not
        // touch NextValue. The forward-skip in ReserveAsync handles the new
        // occupied core when the allocator eventually reaches it.
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        // After two reservations NextValue == 3.

        var response = await service.ChangeInvoiceNumberAsync(first.Id,
            new ChangeBillNumberRequest("50", null, DryRun: false));

        Assert.True(response.Committed);
        Assert.True(response.LeavesGap);
        // currentNext (3) <= max(remaining)+1 (= 51); rollback's min() keeps
        // NextValue at 3, so the next bill picks up /0003 (and forward-skips
        // past /0050 only when the allocator eventually reaches it).
        Assert.Equal(3L, response.SequenceNextValue);
    }

    [Fact]
    public async Task Push_RejectedFirstPushThenEdit_UsesCreateNotAlter()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(new[]
        {
            FailedResponse("TALLY_LINEERROR", "Ledger missing."),
            PostedResponse(remoteId: "201", tallyMasterId: "201")
        });
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var failed = await service.PushAsync(bill.Id, new PushBillRequest("first"));
        Assert.Equal(BillStates.Failed, failed.State);

        var edited = await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-fixed", 100m)));
        Assert.False(edited.EditedAfterPush);

        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("second"));

        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(2, poster.CallCount);
        Assert.Equal(TallyPostOperation.Create, poster.LastRequest!.Operation);
    }

    [Fact]
    public async Task Push_FailedAlterThenEdit_StillAlters()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(new[]
        {
            PostedResponse(remoteId: "101", tallyMasterId: "101"),
            FailedResponse("TALLY_LINEERROR", "Rejected alter."),
            PostedResponse(remoteId: "101", tallyMasterId: "101")
        });
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edit-1", 150m)), callerIsAdmin: true);
        await service.PushAsync(bill.Id, new PushBillRequest("alter-1"));

        var edited = await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edit-2", 160m)));
        Assert.True(edited.EditedAfterPush);
        await service.PushAsync(bill.Id, new PushBillRequest("alter-2"));

        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        Assert.Equal("101", poster.LastRequest.TargetTagValue);
    }

    [Fact]
    public async Task Void_EditedAfterPush_RejectedBecauseTallyHoldsVoucher()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(tallyMasterId: "101"));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.VoidAsync(bill.Id, new VoidBillRequest("customer cancelled")));
        Assert.Equal(BillStates.Pending, (await service.GetAsync(bill.Id))!.State);
    }

    [Fact]
    public async Task Void_FailedFirstPush_Allowed()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(TallyPostOutcome.Failed));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        var voided = await service.VoidAsync(bill.Id, new VoidBillRequest("never reached Tally"));

        Assert.Equal(BillStates.Voided, voided.State);
    }

    [Fact]
    public async Task Void_AfterAdminMarkPending_Allowed()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(tallyMasterId: "101"));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.MarkPendingAsync(bill.Id, new MarkBillStateRequest("voucher deleted in Tally"));

        var voided = await service.VoidAsync(bill.Id, new VoidBillRequest(null));

        Assert.Equal(BillStates.Voided, voided.State);
    }

    [Fact]
    public async Task ChangeInvoiceNumber_PushedBill_ReopensSoNextPushAltersSameVoucher()
    {
        await using var db = CreateDbContext();
        var poster = new FakeTallyPoster(tallyMasterId: "101");
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        var changed = await service.ChangeInvoiceNumberAsync(bill.Id,
            new ChangeBillNumberRequest("7", "typo", DryRun: false));
        var reopened = await service.GetAsync(bill.Id);
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("after-rename"));

        Assert.True(changed.TallyDiverges);
        Assert.Equal(BillStates.Pending, reopened!.State);
        Assert.True(reopened.EditedAfterPush);
        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(TallyPostOperation.Alter, poster.LastRequest!.Operation);
        Assert.Equal("101", poster.LastRequest.TargetTagValue);
        Assert.EndsWith("/0007", poster.LastRequest.InvoiceNumber);
    }

    [Fact]
    public async Task ChangeInvoiceNumber_PushedTrailingBill_DoesNotReclaimNumberTallyStillHolds()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(tallyMasterId: "101"));
        await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        await service.PushAsync(second.Id, new PushBillRequest("first"));

        await service.ChangeInvoiceNumberAsync(second.Id, new ChangeBillNumberRequest("9", null, DryRun: false));
        var next = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));

        Assert.EndsWith("/0003", next.InvoiceNumber);
    }

    [Fact]
    public async Task Delete_PushedTrailingBill_DoesNotReclaimNumberTallyStillHolds()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(tallyMasterId: "101"));
        await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        await service.PushAsync(second.Id, new PushBillRequest("first"));

        await service.DeleteAsync(second.Id, new DeleteBillRequest("removed locally", DryRun: false));
        var next = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));

        Assert.EndsWith("/0003", next.InvoiceNumber);
    }

    [Fact]
    public async Task Delete_PushedBillRenumberedAheadOfTheSeries_ItsNumberIsNotIssuedAgain()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(tallyMasterId: "101"));
        await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        await service.PushAsync(second.Id, new PushBillRequest("first"));

        // The series is at 3. The voucher in Tally is moved to number 5, and
        // the bill is then deleted here while Tally keeps that voucher.
        await service.ChangeInvoiceNumberAsync(second.Id, new ChangeBillNumberRequest("5", null, DryRun: false));
        await service.PushAsync(second.Id, new PushBillRequest("alter"));
        await service.DeleteAsync(second.Id, new DeleteBillRequest("removed locally", DryRun: false));

        var issued = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var preview = await new NumberingService(db).GetPreviewAsync(null);
            var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload($"N{i}", 100m)));
            Assert.Equal(preview.FormattedNumber, bill.InvoiceNumber);
            issued.Add(bill.InvoiceNumber![^5..]);
        }

        Assert.Equal(["/0003", "/0004", "/0006"], issued);
    }

    [Fact]
    public async Task ChangeInvoiceNumber_PushedBillMovedOffANumberAheadOfTheSeries_KeepsThatNumberForTally()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db, new FakeTallyPoster(tallyMasterId: "101"));
        await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        await service.PushAsync(second.Id, new PushBillRequest("first"));
        await service.ChangeInvoiceNumberAsync(second.Id, new ChangeBillNumberRequest("4", null, DryRun: false));
        await service.PushAsync(second.Id, new PushBillRequest("alter"));

        // Tally has the voucher as 4. The bill moves on to 9 and is not pushed
        // again, so Tally still shows 4 when the series reaches it.
        await service.ChangeInvoiceNumberAsync(second.Id, new ChangeBillNumberRequest("9", null, DryRun: false));

        var third = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 100m)));
        var fourth = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("D", 100m)));

        Assert.EndsWith("/0003", third.InvoiceNumber);
        Assert.EndsWith("/0005", fourth.InvoiceNumber);
    }

    [Fact]
    public async Task Push_CallerCancelsDuringTallyCall_StillSettlesForReconciliation()
    {
        await using var db = CreateDbContext();
        using var requestCts = new CancellationTokenSource();
        var poster = new CancellingTallyPoster(requestCts);
        var service = BuildService(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.PushAsync(bill.Id, new PushBillRequest("push"), requestCts.Token);

        Assert.Equal(BillStates.ReconciliationRequired, response.State);
        var persisted = await db.Bills.AsNoTracking().SingleAsync(b => b.Id == bill.Id);
        Assert.Equal(BillStates.ReconciliationRequired, persisted.State);
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == "tally.outcome.unknown");
    }

    [Fact]
    public async Task PushSelected_CallerCancelsDuringFirstBill_DoesNotStartTheNext()
    {
        await using var db = CreateDbContext();
        using var requestCts = new CancellationTokenSource();
        var poster = new CancellingTallyPoster(requestCts);
        var service = BuildService(db, poster);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));

        await service.PushSelectedAsync(new PushSelectedBillsRequest([first.Id, second.Id], "batch"), requestCts.Token);

        Assert.Equal(1, poster.CallCount);
        Assert.Equal(BillStates.Pending, (await db.Bills.AsNoTracking().SingleAsync(b => b.Id == second.Id)).State);
    }

    [Fact]
    public async Task PushSelected_CallerGoesAwayBeforeABillIsSent_IsCutShortAndNotThatBillFailing()
    {
        await using var db = CreateDbContext();
        using var requestCts = new CancellationTokenSource();
        var poster = new FakeTallyPoster();
        // The caller goes away while the bill's voucher is being looked up, before anything is claimed or sent.
        var lookup = new FakeVoucherLookup
        {
            WhenAsked = requestCts.Cancel,
            Failure = new OperationCanceledException(),
        };
        var service = BuildService(db, poster, voucherLookup: lookup);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(SamplePayload("A-edited", 150m)), callerIsAdmin: true);

        var response = await service.PushSelectedAsync(
            new PushSelectedBillsRequest([bill.Id], "batch"), requestCts.Token);

        Assert.True(response.Cancelled);
        Assert.False(response.StoppedOnFailure);
        Assert.Equal(0, response.Failed);
        Assert.Null(response.FailedBillId);
        Assert.Equal(0, poster.CallCount);
        await using var check = CreateDbContext();
        Assert.Equal(BillStates.Pending, (await check.Bills.SingleAsync(b => b.Id == bill.Id)).State);
    }

    [Fact]
    public async Task DeleteSelected_CallerGoesAway_StopsInsteadOfReportingEveryBillAsFailed()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));
        using var requestCts = new CancellationTokenSource();
        requestCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteSelectedAsync(
            new DeleteSelectedBillsRequest(new[] { first.Id, second.Id }, "batch delete"), requestCts.Token));

        await using var check = CreateDbContext();
        Assert.Equal(2, await check.Bills.CountAsync());
    }

    [Fact]
    public async Task PushSelected_CancelledBetweenBills_SaysItWasCutShort()
    {
        await using var db = CreateDbContext();
        using var requestCts = new CancellationTokenSource();
        var service = BuildService(db, new PostThenCancelTallyPoster(requestCts));
        var first = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var second = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 100m)));

        var response = await service.PushSelectedAsync(
            new PushSelectedBillsRequest([first.Id, second.Id], "batch"), requestCts.Token);

        // One posted, none failed: without the flag this reads as a finished run.
        Assert.True(response.Cancelled);
        Assert.False(response.StoppedOnFailure);
        Assert.Equal(2, response.Matched);
        Assert.Equal(1, response.Succeeded);
        Assert.Equal(BillStates.Pending, (await db.Bills.AsNoTracking().SingleAsync(b => b.Id == second.Id)).State);
    }

    [Fact]
    public async Task Retry_WithoutARequest_IsRefusedLikePushAndRepost()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RetryAsync(bill.Id, null!));
    }

    [Fact]
    public async Task Push_TallyTakesLongerThanTheSettleBudget_StillRecordsTheOutcome()
    {
        await using var db = CreateDbContext();
        var clock = new FakeTimeProvider();
        var poster = new SlowTallyPoster(clock, TimeSpan.FromSeconds(31), PostedResponse());
        var service = BuildService(db, poster, timeProvider: clock);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.PushAsync(bill.Id, new PushBillRequest("push"));

        Assert.Equal(BillStates.Posted, response.State);
        var persisted = await db.Bills.AsNoTracking().SingleAsync(b => b.Id == bill.Id);
        Assert.Equal(BillStates.Posted, persisted.State);
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == "tally.posted");
    }

    [Fact]
    public async Task Push_TallyTimesOutAfterTheSettleBudget_MovesToReconciliationRequired()
    {
        await using var db = CreateDbContext();
        var clock = new FakeTimeProvider();
        var poster = new SlowTallyPoster(
            clock,
            TimeSpan.FromSeconds(31),
            UnknownResponse("TALLY_TIMEOUT", "Tally did not answer in time."));
        var service = BuildService(db, poster, timeProvider: clock);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.PushAsync(bill.Id, new PushBillRequest("push"));

        Assert.Equal(BillStates.ReconciliationRequired, response.State);
        var persisted = await db.Bills.AsNoTracking().SingleAsync(b => b.Id == bill.Id);
        Assert.Equal(BillStates.ReconciliationRequired, persisted.State);
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.EventType == "tally.outcome.unknown");
    }

    private sealed class SlowTallyPoster(
        FakeTimeProvider clock,
        TimeSpan duration,
        TallyPostResponse response) : ITallyPoster
    {
        public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
        {
            clock.Advance(duration);
            return Task.FromResult(response);
        }
    }

    private sealed class PostThenCancelTallyPoster(CancellationTokenSource requestCts) : ITallyPoster
    {
        public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
        {
            requestCts.Cancel();
            return Task.FromResult(TallyPostResponse.Posted("remote-1"));
        }
    }

    private sealed class CancellingTallyPoster(CancellationTokenSource requestCts) : ITallyPoster
    {
        public int CallCount { get; private set; }

        public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            requestCts.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // ---------- Mark Posted / Pending ----------

    [Fact]
    public async Task MarkPosted_FromPending_TransitionsAndWritesAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.MarkPostedAsync(bill.Id,
            new MarkBillStateRequest("cash register offline reprint"));

        Assert.Equal(BillStates.Posted, response.State);
        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.mark_posted");
    }

    [Fact]
    public async Task MarkPosted_NullReason_Accepted()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest(null));

        Assert.Equal(BillStates.Posted, response.State);
    }

    [Fact]
    public async Task MarkPending_FromPosted_ClearsEditedAfterPushAndWritesAudit()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = BillStates.Posted;
        entity.EditedAfterPush = true;
        await db.SaveChangesAsync();

        var response = await service.MarkPendingAsync(bill.Id,
            new MarkBillStateRequest("actually never made it to tally"));

        Assert.Equal(BillStates.Pending, response.State);
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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));

        var response = await service.DeleteAsync(bill.Id,
            new DeleteBillRequest("user cancelled", DryRun: false));

        Assert.True(response.Committed);
        Assert.False(response.TallyDiverges);
        Assert.Null(await db.Bills.FirstOrDefaultAsync(b => b.Id == bill.Id));
        Assert.Empty(await db.BillRevisions.Where(r => r.BillId == bill.Id).ToListAsync());
        var audits = await db.AuditEvents.Where(x => x.EntityId == bill.Id.ToString()).ToListAsync();
        Assert.Contains(audits, a => a.EventType == "bill.deleted");
        Assert.NotNull(await service.GetAuditAsync(bill.Id));
    }

    [Fact]
    public async Task Delete_Posted_SucceedsWithTallyDivergesFlag()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = BillStates.Posted;
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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = BillStates.Posted;
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
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var entity = await db.Bills.FirstAsync(b => b.Id == bill.Id);
        entity.State = BillStates.Posting;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BillStateConflictException>(() =>
            service.DeleteAsync(bill.Id, new DeleteBillRequest(null, DryRun: false)));
    }

    [Fact]
    public async Task DeleteSelected_MixedStates_ReportsEachOutcome()
    {
        await using var db = CreateDbContext();
        var service = BuildService(db);
        var pending = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("A", 100m)));
        var posted = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("B", 200m)));
        var postedEntity = await db.Bills.FirstAsync(b => b.Id == posted.Id);
        postedEntity.State = BillStates.Posted;
        var posting = await service.CreateDraftAsync(new CreateBillDraftRequest(SamplePayload("C", 300m)));
        var postingEntity = await db.Bills.FirstAsync(b => b.Id == posting.Id);
        postingEntity.State = BillStates.Posting;
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

    private static DateOnly SampleBillDate => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    /// <summary>
    /// An audit row's <c>details</c>. Parsed, not matched as text: a row read
    /// back from PostgreSQL is jsonb, written with spaces after the colons.
    /// </summary>
    private static System.Text.Json.JsonElement AuditDetails(ShowroomBilling.Infrastructure.Persistence.Entities.AuditEventEntity row) =>
        System.Text.Json.JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("details").Clone();

    private const string TestCompany = "Test Company";

    private static BillPayloadDto SamplePayload(string party, decimal grandTotal) =>
        new(
            PartyName: party,
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: SampleBillDate,
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", grandTotal / 10m, grandTotal, "22K", null)
            ],
            Totals: new BillTotalsDto(grandTotal, 0m, 0m, 0m, grandTotal),
            Notes: null);

    private static BillService BuildService(
        ShowroomBillingDbContext db,
        ITallyPoster? poster = null,
        ITallyCompanyHealthService? tallyHealth = null,
        TimeProvider? timeProvider = null,
        ITallyVoucherLookup? voucherLookup = null,
        bool withoutVoucherLookup = false)
    {
        var numbering = new NumberingService(db);
        return new BillService(
            db,
            numbering,
            poster ?? new FakeTallyPoster(),
            tallyHealth ?? FakeTallyHealth.Healthy(),
            timeProvider: timeProvider,
            // An Alter always reads its voucher from Tally first; by default
            // Tally has every voucher a push recorded, dated as the sample bills are.
            tallyVoucherLookup: withoutVoucherLookup ? null : voucherLookup ?? new FakeVoucherLookup());
    }

    /// <summary>
    /// Tally's answers to "which voucher has this master id" and "which
    /// vouchers carry this number", and what it was asked.
    /// </summary>
    private sealed class FakeVoucherLookup(params string[] numberedMasterIds) : ITallyVoucherLookup
    {
        public Exception? Failure { get; init; }
        public Action? WhenAsked { get; init; }
        /// <summary>The date every voucher has in Tally.</summary>
        public DateOnly VoucherDate { get; init; } = SampleBillDate;
        /// <summary>The total every voucher has in Tally, when Tally gives one.</summary>
        public decimal? VoucherAmount { get; init; }
        /// <summary>The company Settings names, which the lookup asks in.</summary>
        public string? Company { get; init; } = TestCompany;
        /// <summary>The number every voucher carries in Tally.</summary>
        public string? VoucherNumber { get; set; }
        /// <summary>Fails only the read-back after a push, by master id.</summary>
        public Exception? ByIdFailure { get; init; }

        public Task<TallyVoucherDestination> DestinationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TallyVoucherDestination(Company, "Sales"));
        /// <summary>Master ids whose voucher Tally no longer has.</summary>
        public HashSet<string> Gone { get; } = [];
        public List<(string Number, string FiscalYear)> Asked { get; } = [];
        public List<string> AskedById { get; } = [];

        public Task<IReadOnlyList<TallyVoucherRef>> FindByNumberAsync(
            string voucherNumber,
            string fiscalYear,
            CancellationToken cancellationToken = default)
        {
            Asked.Add((voucherNumber, fiscalYear));
            WhenAsked?.Invoke();
            return Failure is null
                ? Task.FromResult<IReadOnlyList<TallyVoucherRef>>(
                    numberedMasterIds.Select(id => new TallyVoucherRef(id, VoucherDate, VoucherAmount, Company, VoucherNumber ?? voucherNumber)).ToList())
                : Task.FromException<IReadOnlyList<TallyVoucherRef>>(Failure);
        }

        public Task<TallyVoucherRef?> FindByMasterIdAsync(
            string masterId,
            string fiscalYear,
            CancellationToken cancellationToken = default)
        {
            AskedById.Add(masterId);
            WhenAsked?.Invoke();
            var failure = Failure ?? ByIdFailure;
            return failure is null
                ? Task.FromResult(Gone.Contains(masterId) ? null : new TallyVoucherRef(masterId, VoucherDate, VoucherAmount, Company, VoucherNumber))
                : Task.FromException<TallyVoucherRef?>(failure);
        }

        public Task<TallyVoucherList> ListSalesVouchersAsync(
            string fiscalYear,
            CancellationToken cancellationToken = default) =>
            Failure is null
                ? Task.FromResult(new TallyVoucherList(new TallyVoucherDestination(Company, "Sales"), []))
                : Task.FromException<TallyVoucherList>(Failure);
    }

    private static void AssertVersion7(Guid id) =>
        Assert.Equal('7', id.ToString("N")[12]);

    private static TallyPostResponse PostedResponse(string? remoteId = "FAKE-VCH-1", string? tallyMasterId = null) =>
        TallyPostResponse.Posted(remoteId, tallyMasterId: tallyMasterId);

    private static TallyPostResponse FailedResponse(string errorCode, string errorMessage) =>
        TallyPostResponse.Failed(errorCode, errorMessage);

    private static TallyPostResponse UnknownResponse(string errorCode, string errorMessage) =>
        TallyPostResponse.Unknown(errorCode, errorMessage);

    internal sealed class FakeTallyPoster : ITallyPoster
    {
        private readonly TallyPostOutcome outcome;
        private readonly string? remoteId;
        private readonly string? tallyMasterId;
        private readonly Queue<TallyPostResponse> responses = new();
        private readonly List<TallyPostRequest> requests = [];

        public FakeTallyPoster(
            TallyPostOutcome outcome = TallyPostOutcome.Posted,
            string? remoteId = "FAKE-VCH-1",
            string? tallyMasterId = null)
        {
            this.outcome = outcome;
            this.remoteId = remoteId;
            this.tallyMasterId = tallyMasterId;
        }

        public FakeTallyPoster(IEnumerable<TallyPostResponse> responses)
            : this()
        {
            foreach (var response in responses)
            {
                this.responses.Enqueue(response);
            }
        }

        public int CallCount { get; private set; }
        public TallyPostRequest? LastRequest { get; private set; }
        public IReadOnlyList<TallyPostRequest> Requests => requests;
        /// <summary>
        /// The company an accepted voucher went to, as the real poster reports
        /// it; the one <see cref="FakeVoucherLookup"/> asks in by default.
        /// Null is a push recorded before pushes named their company.
        /// </summary>
        public string? Company { get; init; } = TestCompany;

        public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            requests.Add(request);
            var response = responses.Count > 0
                ? responses.Dequeue()
                : outcome switch
                {
                    // Tally answers an Alter with the id of the voucher it altered.
                    TallyPostOutcome.Posted => PostedResponse(
                        remoteId,
                        request.Operation == TallyPostOperation.Alter ? request.TargetTagValue : tallyMasterId),
                    TallyPostOutcome.Unknown => UnknownResponse("FAKE_UNKNOWN", "fake outcome unknown"),
                    _ => FailedResponse("FAKE_ERROR", "fake failure")
                };
            return Task.FromResult(response.Outcome == TallyPostOutcome.Posted && Company is not null
                ? response with { Company = Company }
                : response);
        }
    }

    private sealed class FakeTallyHealth(TallyCompanyHealthResponse response) : ITallyCompanyHealthService
    {
        public static FakeTallyHealth Healthy() =>
            new(new TallyCompanyHealthResponse(
                Status: "healthy",
                TallyReachable: true,
                ActiveCompanyName: "Test Company",
                ActiveCompanyOpen: true,
                CompanyCount: 1,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                Message: "Tally OK - active company 'Test Company' is open."));

        public static FakeTallyHealth Unhealthy(string message) =>
            new(new TallyCompanyHealthResponse(
                Status: "unhealthy",
                TallyReachable: false,
                ActiveCompanyName: "Test Company",
                ActiveCompanyOpen: false,
                CompanyCount: 0,
                CheckedAtUtc: DateTimeOffset.UtcNow,
                Message: message));

        public Task<TallyCompanyHealthResponse> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(response);
    }
}
