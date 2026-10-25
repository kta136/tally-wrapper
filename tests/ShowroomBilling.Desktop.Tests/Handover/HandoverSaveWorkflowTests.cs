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
    private readonly List<BillResponse> _printed = [];
    private readonly List<string> _adminAsked = [];
    private string? _adminToken;
    private bool _printerWorks = true;

    private HandoverSaveWorkflow Workflow() => new(
        _api,
        (_, context, _) =>
        {
            _adminAsked.Add(context);
            return Task.FromResult(_adminToken);
        },
        bill =>
        {
            _printed.Add(bill);
            return Task.FromResult(_printerWorks);
        });

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
        Assert.Null(create.CounterId);
        Assert.True(HandoverPayloads.SameContent(PayloadOf(document), create.Payload));
        Assert.Equal([$"by-operation:{saveId}"], _api.Calls);
        Assert.Single(_printed);
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
        Assert.Empty(_printed);
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
        Assert.Single(_printed);
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
        Assert.Empty(_printed);
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
        Assert.Single(_printed);
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
        Assert.Empty(_printed);
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
        Assert.Single(_printed);
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
        Assert.Single(_printed);
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
}
