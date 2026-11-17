using System.Net;
using System.Net.Http;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Handover;

namespace ShowroomBilling.Desktop.Tests.Handover;

public sealed class HandoverSaveWorkflowTests
{
    private readonly FakeBillsApi _api = new();

    /// <summary>Each time the printer was handed bills: which, and how far the saving and asking had got by then.</summary>
    private readonly List<(IReadOnlyList<BillResponse> Bills, int SavedBefore, int AskedBefore)> _jobs = [];
    private readonly List<string> _adminAsked = [];
    private string? _adminToken;

    /// <summary>An admin session is open already, so the operator is not asked.</summary>
    private bool _adminAlreadyUnlocked;
    private bool _printerWorks = true;

    /// <summary>Every bill handed to the printer, in order.</summary>
    private List<BillResponse> Printed => _jobs.SelectMany(job => job.Bills).ToList();

    private HandoverSaveWorkflow Workflow(Dictionary<Guid, HandoverBillResult>? unanswered = null) => new(
        _api,
        async (_, context, beforeAsking, _) =>
        {
            if (!_adminAlreadyUnlocked)
            {
                await beforeAsking();
                _adminAsked.Add(context);
            }

            return _adminToken;
        },
        bills =>
        {
            _jobs.Add((bills, _api.Creates.Count + _api.Updates.Count, _adminAsked.Count));
            return Task.FromResult<IReadOnlyList<bool>>(bills.Select(_ => _printerWorks).ToList());
        },
        unanswered);

    private static string PartyOf(BillResponse bill) => bill.CurrentRevision!.Payload.PartyName!;

    private static HandoverDocument Document(IEnumerable<Guid>? notSent = null, params Guid[] saveIds)
        => HandoverTestLinks.Read(HandoverTestLinks.Document(
            DateTimeOffset.UtcNow,
            saveIds.Select((id, index) => HandoverTestLinks.Bill(id, $"Buyer {(char)('A' + index)}", HandoverTestLinks.Line())),
            notSent));

    private static BillPayloadDto PayloadOf(HandoverDocument document, int index = 0)
        => HandoverBillMapper.Map(document.Bills[index], HandoverTestMasters.Masters).Payload!;

    private static BillResponse Stored(string state, BillPayloadDto payload, string number = "SR/2026-27/7")
        => FakeBillsApi.Bill(Guid.NewGuid(), state, payload, number);

    private Task<HandoverRun> RunAsync(HandoverDocument document, HandoverSaveWorkflow? workflow = null)
        => (workflow ?? Workflow()).RunAsync(document, HandoverTestMasters.Masters, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_bill_not_saved_before_is_created_with_its_save_id_and_printed()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);

        var run = await RunAsync(document);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(HandoverBillResult.Saved, outcome.Result);
        Assert.Equal(HandoverPrintResult.Printed, outcome.Print);
        var create = Assert.Single(_api.Creates);
        Assert.Equal(saveId, create.ClientOperationId);
        Assert.True(HandoverPayloads.SameContent(PayloadOf(document), create.Payload));
        Assert.Equal([$"by-operation:{saveId}"], _api.Calls);
        Assert.Single(Printed);
        Assert.False(run.ServerUnreachable);
    }

    [Fact]
    public async Task A_bill_saved_before_with_the_same_content_is_left_alone_and_not_printed()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        // Stored with its decimals' trailing zeros, as the server's JSON may give them back.
        var payload = PayloadOf(document);
        var stored = payload with { Lines = payload.Lines.Select(line => line with { LineTotal = line.LineTotal * 1.00m }).ToList() };
        _api.ByOperation[saveId] = Stored(BillStates.Pending, stored);

        var run = await RunAsync(document);

        Assert.Equal(HandoverBillResult.Unchanged, Assert.Single(run.Outcomes).Result);
        Assert.Empty(_api.Creates);
        Assert.Empty(_api.Updates);
        Assert.Empty(Printed);
    }

    [Fact]
    public async Task A_bill_saved_before_with_other_content_is_updated_at_the_revision_found_and_printed()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        var older = PayloadOf(document) with { PartyName = "Buyer Z" };
        var stored = Stored(BillStates.Failed, older);
        _api.ByOperation[saveId] = stored;

        var run = await RunAsync(document);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(HandoverBillResult.Updated, outcome.Result);
        Assert.Equal(HandoverPrintResult.Printed, outcome.Print);
        var update = Assert.Single(_api.Updates);
        Assert.Equal(stored.Id, update.BillId);
        Assert.Equal(stored.CurrentRevisionId, update.Request.ExpectedRevisionId);
        Assert.Null(update.AdminToken);
        Assert.Equal("Buyer A", update.Request.Payload.PartyName);
        Assert.Empty(_adminAsked);
        Assert.Single(Printed);
    }

    [Fact]
    public async Task A_posted_bill_asks_for_the_admin_passcode_and_is_skipped_without_it()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        _api.ByOperation[saveId] = Stored(BillStates.Posted, PayloadOf(document) with { PartyName = "Buyer Z" }, "SR/2026-27/12");

        var run = await RunAsync(document);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(HandoverBillResult.Skipped, outcome.Result);
        Assert.Equal("SR/2026-27/12 · Buyer A is in Tally. Updating it needs the admin passcode.", Assert.Single(_adminAsked));
        Assert.Empty(_api.Updates);
        Assert.Empty(Printed);
    }

    [Fact]
    public async Task A_posted_bill_is_updated_with_the_admin_session_once_unlocked()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        _api.ByOperation[saveId] = Stored(BillStates.Posted, PayloadOf(document) with { PartyName = "Buyer Z" });
        _adminToken = "admin-session";

        var run = await RunAsync(document);

        Assert.Equal(HandoverBillResult.Updated, Assert.Single(run.Outcomes).Result);
        Assert.Equal("admin-session", Assert.Single(_api.Updates).AdminToken);
        Assert.Single(Printed);
    }

    [Theory]
    [InlineData(BillStates.Voided)]
    [InlineData(BillStates.Revised)]
    [InlineData(BillStates.Posting)]
    [InlineData(BillStates.ReconciliationRequired)]
    public async Task A_bill_voided_revised_or_waiting_on_Tally_is_reported_and_no_second_bill_is_made(string state)
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        _api.ByOperation[saveId] = Stored(state, PayloadOf(document) with { PartyName = "Buyer Z" });

        var run = await RunAsync(document);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(HandoverBillResult.NotUpdated, outcome.Result);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
        Assert.Equal("SR/2026-27/7", outcome.InvoiceNumber);
        Assert.Empty(_api.Creates);
        Assert.Empty(_api.Updates);
        Assert.Empty(Printed);
    }

    [Fact]
    public async Task A_voided_bill_with_the_same_content_is_still_reported_not_unchanged()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        _api.ByOperation[saveId] = Stored(BillStates.Voided, PayloadOf(document));

        var run = await RunAsync(document);

        Assert.Equal(HandoverBillResult.NotUpdated, Assert.Single(run.Outcomes).Result);
    }

    [Fact]
    public async Task Bills_left_out_are_reported_when_still_held_and_never_changed()
    {
        var kept = Guid.NewGuid();
        var leftLive = Guid.NewGuid();
        var leftVoided = Guid.NewGuid();
        var leftUnknown = Guid.NewGuid();
        var document = Document([leftLive, leftVoided, leftUnknown], kept);
        _api.ByOperation[leftLive] = Stored(BillStates.Posted, PayloadOf(document), "SR/2026-27/9");
        _api.ByOperation[leftVoided] = Stored(BillStates.Voided, PayloadOf(document), "SR/2026-27/10");

        var run = await RunAsync(document);

        Assert.Equal(2, run.Outcomes.Count);
        var left = run.Outcomes[1];
        Assert.Equal(HandoverBillResult.NotInHandover, left.Result);
        Assert.Equal("SR/2026-27/9", left.InvoiceNumber);
        Assert.Equal("Void it in Bills if it’s no longer wanted.", left.Reason);
        Assert.Single(_api.Creates);
        Assert.Empty(_api.Updates);
        Assert.Single(Printed);
    }

    [Fact]
    public async Task A_bill_that_cannot_be_matched_is_not_saved_and_the_next_one_still_is()
    {
        var document = HandoverTestLinks.Read(HandoverTestLinks.Document(
            DateTimeOffset.UtcNow,
            [
                HandoverTestLinks.Bill(Guid.NewGuid(), "Buyer A", HandoverTestLinks.Line(item: "Silver Bowl")),
                HandoverTestLinks.Bill(Guid.NewGuid(), "Buyer B", HandoverTestLinks.Line()),
            ]));

        var run = await RunAsync(document);

        Assert.Equal(HandoverBillResult.NotSaved, run.Outcomes[0].Result);
        Assert.Equal("No item named “Silver Bowl” in Tally Wrapper’s items.", run.Outcomes[0].Reason);
        Assert.Equal(HandoverBillResult.Saved, run.Outcomes[1].Result);
        Assert.Single(_api.Creates);
    }

    [Fact]
    public async Task When_the_server_cannot_be_reached_nothing_more_is_tried()
    {
        _api.ByOperationFailure = new HttpRequestException("No connection could be made");
        var document = Document([Guid.NewGuid()], Guid.NewGuid(), Guid.NewGuid());

        var run = await RunAsync(document);

        Assert.True(run.ServerUnreachable);
        Assert.True(run.CanTryAgain);
        Assert.All(run.Outcomes, outcome => Assert.Equal(HandoverBillResult.NotSaved, outcome.Result));
        Assert.Equal(2, run.Outcomes.Count);
        Assert.Single(_api.Calls);
        Assert.Empty(_api.Creates);
    }

    [Fact]
    public async Task A_refusal_from_the_server_is_the_reason_and_not_an_unreachable_server()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        _api.ByOperation[saveId] = Stored(BillStates.Pending, PayloadOf(document) with { PartyName = "Buyer Z" });
        _api.FailWith = new ApiException(HttpStatusCode.Conflict, "Concurrent change", "Bill SR/2026-27/7 was changed elsewhere.", null);

        var run = await RunAsync(document);

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(HandoverBillResult.NotUpdated, outcome.Result);
        Assert.Equal("Bill SR/2026-27/7 was changed elsewhere.", outcome.Reason);
        Assert.False(run.ServerUnreachable);
        Assert.False(run.CanTryAgain);
    }

    [Fact]
    public async Task A_save_whose_answer_was_lost_is_printed_when_Try_again_finds_it()
    {
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        var workflow = Workflow();
        _api.FailNextCreates = 1;

        var first = await RunAsync(document, workflow);
        // The create did land; the server now finds it.
        _api.ByOperation[saveId] = Stored(BillStates.Pending, _api.Creates[0].Payload);
        var second = await RunAsync(document, workflow);

        Assert.True(first.ServerUnreachable);
        Assert.Equal(HandoverBillResult.NotSaved, Assert.Single(first.Outcomes).Result);
        var outcome = Assert.Single(second.Outcomes);
        Assert.Equal(HandoverBillResult.Saved, outcome.Result);
        Assert.Equal(HandoverPrintResult.Printed, outcome.Print);
        Assert.Single(Printed);
    }

    [Fact]
    public async Task A_save_whose_answer_was_lost_is_printed_when_the_bills_are_handed_over_afresh()
    {
        // The operator closes the result instead of pressing Try again, and
        // the sender hands the same bills over once more: a new hand-over,
        // with the app's one list of saves that got no answer.
        var saveId = Guid.NewGuid();
        var document = Document(null, saveId);
        var unanswered = new Dictionary<Guid, HandoverBillResult>();
        HandoverSaveWorkflow NewHandover() => Workflow(unanswered);
        _api.FailNextCreates = 1;

        await RunAsync(document, NewHandover());
        _api.ByOperation[saveId] = Stored(BillStates.Pending, _api.Creates[0].Payload);
        var afresh = await RunAsync(document, NewHandover());

        var outcome = Assert.Single(afresh.Outcomes);
        Assert.Equal(HandoverBillResult.Saved, outcome.Result);
        Assert.Equal(HandoverPrintResult.Printed, outcome.Print);
        Assert.Single(Printed);

        // Printed once: a third hand-over finds it unchanged.
        var again = await RunAsync(document, NewHandover());
        Assert.Equal(HandoverBillResult.Unchanged, Assert.Single(again.Outcomes).Result);
        Assert.Single(Printed);
    }

    [Fact]
    public async Task A_bill_that_did_not_print_is_saved_and_says_so()
    {
        _printerWorks = false;

        var run = await RunAsync(Document(null, Guid.NewGuid()));

        var outcome = Assert.Single(run.Outcomes);
        Assert.Equal(HandoverBillResult.Saved, outcome.Result);
        Assert.Equal(HandoverPrintResult.Failed, outcome.Print);
        Assert.NotNull(outcome.Bill);
    }

    [Fact]
    public async Task Bills_are_saved_one_after_another_in_the_hand_overs_order()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        var run = await RunAsync(Document(null, ids));

        Assert.Equal(ids, _api.Creates.Select(create => create.ClientOperationId!.Value));
        Assert.Equal(["Buyer A", "Buyer B", "Buyer C"], run.Outcomes.Select(outcome => outcome.PartyName));
    }

    [Fact]
    public async Task Every_bill_is_saved_before_any_is_printed_and_they_go_to_the_printer_together_in_order()
    {
        var run = await RunAsync(Document(null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        // One hand to the printer, with all three, made when the third had been saved. Each
        // bill used to be printed before the next was even looked up.
        var job = Assert.Single(_jobs);
        Assert.Equal(3, job.SavedBefore);
        Assert.Equal(["Buyer A", "Buyer B", "Buyer C"], job.Bills.Select(PartyOf));
        Assert.All(run.Outcomes, outcome =>
        {
            Assert.Equal(HandoverBillResult.Saved, outcome.Result);
            Assert.Equal(HandoverPrintResult.Printed, outcome.Print);
        });
    }

    [Fact]
    public async Task A_hand_over_of_one_bill_hands_the_printer_that_one_bill()
    {
        await RunAsync(Document(null, Guid.NewGuid()));

        Assert.Equal(["Buyer A"], Assert.Single(_jobs).Bills.Select(PartyOf));
    }

    [Fact]
    public async Task When_the_print_fails_every_bill_handed_to_it_is_saved_and_not_printed()
    {
        _printerWorks = false;

        var run = await RunAsync(Document(null, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Single(_jobs);
        Assert.All(run.Outcomes, outcome =>
        {
            Assert.Equal(HandoverBillResult.Saved, outcome.Result);
            Assert.Equal(HandoverPrintResult.Failed, outcome.Print);

            // Kept, so that the bills can be opened together in Print preview.
            Assert.NotNull(outcome.Bill);
        });
    }

    [Fact]
    public async Task Each_bill_keeps_its_own_print_result_when_the_printer_took_only_some()
    {
        // As when they go as a job each (two-sided printing) and one job fails, or the printer
        // answers for fewer bills than it was handed, or the print throws.
        async Task<HandoverRun> RunWithPrinterAsync(Func<IReadOnlyList<BillResponse>, Task<IReadOnlyList<bool>>> print)
            => await new HandoverSaveWorkflow(new FakeBillsApi(), (_, _, _, _) => Task.FromResult<string?>(null), print)
                .RunAsync(Document(null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), HandoverTestMasters.Masters, TestContext.Current.CancellationToken);

        var some = await RunWithPrinterAsync(_ => Task.FromResult<IReadOnlyList<bool>>([true, false, true]));
        var fewer = await RunWithPrinterAsync(_ => Task.FromResult<IReadOnlyList<bool>>([true]));
        var thrown = await RunWithPrinterAsync(_ => throw new InvalidOperationException("The printer is offline."));

        HandoverPrintResult[] Prints(HandoverRun run) => run.Outcomes.Select(outcome => outcome.Print).ToArray();
        Assert.Equal([HandoverPrintResult.Printed, HandoverPrintResult.Failed, HandoverPrintResult.Printed], Prints(some));
        Assert.Equal([HandoverPrintResult.Printed, HandoverPrintResult.Failed, HandoverPrintResult.Failed], Prints(fewer));
        Assert.Equal([HandoverPrintResult.Failed, HandoverPrintResult.Failed, HandoverPrintResult.Failed], Prints(thrown));
        Assert.All(thrown.Outcomes, outcome => Assert.Equal(HandoverBillResult.Saved, outcome.Result));
    }

    [Fact]
    public async Task Only_the_created_and_updated_bills_go_to_the_printer()
    {
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var document = Document(null, ids);
        // A is new. B is there unchanged. C was voided. D's push is not settled. E is in Tally
        // and the operator skips the passcode. F is there with other content.
        _api.ByOperation[ids[1]] = Stored(BillStates.Pending, PayloadOf(document, 1));
        _api.ByOperation[ids[2]] = Stored(BillStates.Voided, PayloadOf(document, 2) with { Notes = "older" });
        _api.ByOperation[ids[3]] = Stored(BillStates.ReconciliationRequired, PayloadOf(document, 3) with { Notes = "older" });
        _api.ByOperation[ids[4]] = Stored(BillStates.Posted, PayloadOf(document, 4) with { Notes = "older" });
        _api.ByOperation[ids[5]] = Stored(BillStates.Failed, PayloadOf(document, 5) with { Notes = "older" });

        var run = await RunAsync(document);

        Assert.Equal(
            [
                HandoverBillResult.Saved, HandoverBillResult.Unchanged, HandoverBillResult.NotUpdated,
                HandoverBillResult.NotUpdated, HandoverBillResult.Skipped, HandoverBillResult.Updated,
            ],
            run.Outcomes.Select(outcome => outcome.Result));
        Assert.Equal(["Buyer A", "Buyer F"], Printed.Select(PartyOf));
        Assert.Equal(
            [
                HandoverPrintResult.Printed, HandoverPrintResult.None, HandoverPrintResult.None,
                HandoverPrintResult.None, HandoverPrintResult.None, HandoverPrintResult.Printed,
            ],
            run.Outcomes.Select(outcome => outcome.Print));
    }

    [Fact]
    public async Task When_the_server_stops_answering_part_way_the_bills_saved_before_are_still_printed()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var document = Document(null, ids);
        // A is created. B is found and has to be updated, and that update gets no answer.
        _api.ByOperation[ids[1]] = Stored(BillStates.Pending, PayloadOf(document, 1) with { Notes = "older" });
        _api.FailWith = new HttpRequestException("No connection could be made");

        var run = await RunAsync(document);

        Assert.True(run.ServerUnreachable);
        Assert.Equal(
            [HandoverBillResult.Saved, HandoverBillResult.NotUpdated, HandoverBillResult.NotSaved],
            run.Outcomes.Select(outcome => outcome.Result));
        Assert.Equal(["Buyer A"], Assert.Single(_jobs).Bills.Select(PartyOf));
        Assert.Equal(HandoverPrintResult.Printed, run.Outcomes[0].Print);
    }

    [Fact]
    public async Task Bills_saved_before_the_passcode_is_asked_for_are_printed_before_the_question()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var document = Document(null, ids);
        // A and B are new; C is in Tally, so updating it asks for the passcode; D is new.
        _api.ByOperation[ids[2]] = Stored(BillStates.Posted, PayloadOf(document, 2) with { Notes = "older" });
        _adminToken = "admin-session";

        var run = await RunAsync(document);

        // A and B are at the printer while the operator is at the passcode: handed over when
        // the question had not been put yet. C and D follow together once it is answered.
        Assert.Equal(2, _jobs.Count);
        Assert.Equal(["Buyer A", "Buyer B"], _jobs[0].Bills.Select(PartyOf));
        Assert.Equal(0, _jobs[0].AskedBefore);
        Assert.Equal(["Buyer C", "Buyer D"], _jobs[1].Bills.Select(PartyOf));
        Assert.Equal(1, _jobs[1].AskedBefore);
        Assert.All(run.Outcomes, outcome => Assert.Equal(HandoverPrintResult.Printed, outcome.Print));
    }

    [Fact]
    public async Task With_an_admin_session_already_open_nobody_is_asked_and_the_bills_stay_together()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var document = Document(null, ids);
        _api.ByOperation[ids[1]] = Stored(BillStates.Posted, PayloadOf(document, 1) with { Notes = "older" });
        _adminToken = "admin-session";
        _adminAlreadyUnlocked = true;

        await RunAsync(document);

        Assert.Empty(_adminAsked);
        Assert.Equal(["Buyer A", "Buyer B", "Buyer C"], Assert.Single(_jobs).Bills.Select(PartyOf));
    }

    [Fact]
    public async Task A_save_whose_answer_was_lost_goes_to_the_printer_with_the_bills_saved_now()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var document = Document(null, ids);
        var workflow = Workflow();
        _api.FailNextCreates = 1;

        await RunAsync(document, workflow);
        // A's create did land; the server now finds it. Try again saves B as well.
        _api.ByOperation[ids[0]] = Stored(BillStates.Pending, _api.Creates[0].Payload);
        var second = await RunAsync(document, workflow);

        Assert.Equal(["Buyer A", "Buyer B"], Assert.Single(_jobs).Bills.Select(PartyOf));
        Assert.All(second.Outcomes, outcome => Assert.Equal(HandoverPrintResult.Printed, outcome.Print));
    }
}
