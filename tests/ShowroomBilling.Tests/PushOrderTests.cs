using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Tests.Postgres;
using ShowroomBilling.Tests.TestSupport;
using static ShowroomBilling.Tests.TestSupport.BillTestData;

namespace ShowroomBilling.Tests;

/// <summary>
/// <c>POST /api/bills/push-order</c>: before a push or retry, the bills that should reach Tally
/// first. Tally numbers a day's vouchers in the order they arrive, so a bill pushed past an
/// earlier-numbered bill of its day, or of an earlier day, is filed under an earlier number.
/// </summary>
public sealed class PushOrderTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    // Fixed days of one fiscal year, so the tests do not depend on today's date.
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly DateOnly Earlier = new(2026, 10, 10);

    [Fact]
    public async Task An_earlier_numbered_bill_of_the_same_day_not_in_Tally_is_named()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db);
        var first = await Create(bills, Today);
        var second = await Create(bills, Today);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([second.Id]));

        var earlier = Assert.Single(order.EarlierBills);
        Assert.Equal((first.Id, first.InvoiceNumber, Today, BillStates.Pending), (earlier.BillId, earlier.InvoiceNumber, earlier.BillDate, earlier.State));
    }

    [Fact]
    public async Task An_earlier_numbered_bill_of_an_earlier_day_is_named_too()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db);
        var first = await Create(bills, Earlier);
        var second = await Create(bills, Today);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([second.Id]));

        Assert.Equal([first.Id], order.EarlierBills.Select(b => b.BillId));
    }

    [Fact]
    public async Task Bills_with_later_numbers_or_later_dates_are_not_named()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db);
        // An earlier number dated after the bill pushed: Tally puts it after, whenever it goes.
        await Create(bills, Today);
        var pushed = await Create(bills, Earlier);
        await Create(bills, Today);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([pushed.Id]));

        Assert.Empty(order.EarlierBills);
    }

    [Fact]
    public async Task Bills_already_in_Tally_or_edited_after_a_push_are_not_named()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db, new FakeTallyPoster(tallyMasterId: "101"));
        var posted = await Create(bills, Today);
        await bills.Posting.PushAsync(posted.Id, new PushBillRequest("first"));
        var marked = await Create(bills, Today);
        await bills.Admin.MarkPostedAsync(marked.Id, new MarkBillStateRequest("entered in Tally by hand"));
        var edited = await Create(bills, Today);
        await bills.Posting.PushAsync(edited.Id, new PushBillRequest("first"));
        await bills.Lifecycle.UpdateDraftAsync(edited.Id, new UpdateBillDraftRequest(Payload(Today, 150m)), callerIsAdmin: true);
        var last = await Create(bills, Today);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([last.Id]));

        Assert.Empty(order.EarlierBills);
    }

    [Fact]
    public async Task A_failed_bill_is_not_in_Tally_and_is_named()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db, new FakeTallyPoster([TallyPostResponse.Failed("LINEERROR", "Ledger missing.")]));
        var failed = await Create(bills, Today);
        await bills.Posting.PushAsync(failed.Id, new PushBillRequest("first"));
        var next = await Create(bills, Today);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([next.Id]));

        Assert.Equal((failed.Id, BillStates.Failed), (Assert.Single(order.EarlierBills).BillId, order.EarlierBills[0].State));
    }

    [Fact]
    public async Task The_bills_asked_about_are_not_named_and_the_rest_come_oldest_first()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db);
        var a = await Create(bills, Earlier);
        var b = await Create(bills, Today);
        var c = await Create(bills, Today);
        var d = await Create(bills, Today);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([d.Id, b.Id]));

        Assert.Equal([a.Id, c.Id], order.EarlierBills.Select(x => x.BillId));
    }

    [Fact]
    public async Task A_bill_whose_push_alters_its_voucher_asks_for_nothing()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db, new FakeTallyPoster(tallyMasterId: "101"));
        await Create(bills, Today);
        var edited = await Create(bills, Today);
        await bills.Admin.MarkPostedAsync(edited.Id, new MarkBillStateRequest("in Tally"));
        await bills.Lifecycle.UpdateDraftAsync(edited.Id, new UpdateBillDraftRequest(Payload(Today, 150m)), callerIsAdmin: true);

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([edited.Id]));

        Assert.Empty(order.EarlierBills);
    }

    [Fact]
    public async Task The_bills_asked_about_come_back_by_date_then_number()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db);
        var a = await Create(bills, Today);
        var b = await Create(bills, Earlier);
        var c = await Create(bills, Today);
        var unknown = Guid.NewGuid();

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([c.Id, unknown, a.Id, b.Id]));

        Assert.Equal([b.Id, a.Id, c.Id, unknown], order.PushOrder);
    }

    [Fact]
    public async Task A_bill_with_no_date_is_held_to_its_number()
    {
        await using var db = CreateDbContext();
        var bills = new BillWorkflows(db);
        var first = await Create(bills, Today);
        var second = await Create(bills, Today);
        // A revision saved without a date (rows from before dates were required).
        var revisionId = await db.Bills.Where(bill => bill.Id == first.Id).Select(bill => bill.CurrentRevisionId).SingleAsync();
        await db.BillRevisions
            .Where(r => r.Id == revisionId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.BillDate, (DateOnly?)null));

        var order = await bills.Read.FindEarlierUnpushedAsync(new BillPushOrderRequest([second.Id]));

        var earlier = Assert.Single(order.EarlierBills);
        Assert.Equal((first.Id, (DateOnly?)null), (earlier.BillId, earlier.BillDate));
    }

    [Fact]
    public async Task More_than_two_hundred_bills_is_refused()
    {
        await using var db = CreateDbContext();
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => new BillWorkflows(db).Read.FindEarlierUnpushedAsync(new BillPushOrderRequest(ids)));
    }

    private static Task<BillResponse> Create(BillWorkflows bills, DateOnly date) =>
        bills.Lifecycle.CreateDraftAsync(new CreateBillDraftRequest(Payload(date, 100m)));

    private static BillPayloadDto Payload(DateOnly date, decimal grandTotal) =>
        SamplePayload("Customer", grandTotal, date);
}
