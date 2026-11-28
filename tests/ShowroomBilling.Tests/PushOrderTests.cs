using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
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
        var service = Service(db);
        var first = await Create(service, Today);
        var second = await Create(service, Today);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([second.Id]));

        var earlier = Assert.Single(order.EarlierBills);
        Assert.Equal((first.Id, first.InvoiceNumber, Today, BillStates.Pending), (earlier.BillId, earlier.InvoiceNumber, earlier.BillDate, earlier.State));
    }

    [Fact]
    public async Task An_earlier_numbered_bill_of_an_earlier_day_is_named_too()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var first = await Create(service, Earlier);
        var second = await Create(service, Today);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([second.Id]));

        Assert.Equal([first.Id], order.EarlierBills.Select(b => b.BillId));
    }

    [Fact]
    public async Task Bills_with_later_numbers_or_later_dates_are_not_named()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        // An earlier number dated after the bill pushed: Tally puts it after, whenever it goes.
        await Create(service, Today);
        var pushed = await Create(service, Earlier);
        await Create(service, Today);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([pushed.Id]));

        Assert.Empty(order.EarlierBills);
    }

    [Fact]
    public async Task Bills_already_in_Tally_or_edited_after_a_push_are_not_named()
    {
        await using var db = CreateDbContext();
        var service = Service(db, new FakeTallyPoster(tallyMasterId: "101"));
        var posted = await Create(service, Today);
        await service.PushAsync(posted.Id, new PushBillRequest("first"));
        var marked = await Create(service, Today);
        await service.MarkPostedAsync(marked.Id, new MarkBillStateRequest("entered in Tally by hand"));
        var edited = await Create(service, Today);
        await service.PushAsync(edited.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(edited.Id, new UpdateBillDraftRequest(Payload(Today, 150m)), callerIsAdmin: true);
        var last = await Create(service, Today);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([last.Id]));

        Assert.Empty(order.EarlierBills);
    }

    [Fact]
    public async Task A_failed_bill_is_not_in_Tally_and_is_named()
    {
        await using var db = CreateDbContext();
        var service = Service(db, new FakeTallyPoster([TallyPostResponse.Failed("LINEERROR", "Ledger missing.")]));
        var failed = await Create(service, Today);
        await service.PushAsync(failed.Id, new PushBillRequest("first"));
        var next = await Create(service, Today);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([next.Id]));

        Assert.Equal((failed.Id, BillStates.Failed), (Assert.Single(order.EarlierBills).BillId, order.EarlierBills[0].State));
    }

    [Fact]
    public async Task The_bills_asked_about_are_not_named_and_the_rest_come_oldest_first()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var a = await Create(service, Earlier);
        var b = await Create(service, Today);
        var c = await Create(service, Today);
        var d = await Create(service, Today);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([d.Id, b.Id]));

        Assert.Equal([a.Id, c.Id], order.EarlierBills.Select(x => x.BillId));
    }

    [Fact]
    public async Task A_bill_whose_push_alters_its_voucher_asks_for_nothing()
    {
        await using var db = CreateDbContext();
        var service = Service(db, new FakeTallyPoster(tallyMasterId: "101"));
        await Create(service, Today);
        var edited = await Create(service, Today);
        await service.MarkPostedAsync(edited.Id, new MarkBillStateRequest("in Tally"));
        await service.UpdateDraftAsync(edited.Id, new UpdateBillDraftRequest(Payload(Today, 150m)), callerIsAdmin: true);

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([edited.Id]));

        Assert.Empty(order.EarlierBills);
    }

    [Fact]
    public async Task The_bills_asked_about_come_back_by_date_then_number()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var a = await Create(service, Today);
        var b = await Create(service, Earlier);
        var c = await Create(service, Today);
        var unknown = Guid.NewGuid();

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([c.Id, unknown, a.Id, b.Id]));

        Assert.Equal([b.Id, a.Id, c.Id, unknown], order.PushOrder);
    }

    [Fact]
    public async Task A_bill_with_no_date_is_held_to_its_number()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var first = await Create(service, Today);
        var second = await Create(service, Today);
        // A revision saved without a date (rows from before dates were required).
        var revisionId = await db.Bills.Where(bill => bill.Id == first.Id).Select(bill => bill.CurrentRevisionId).SingleAsync();
        await db.BillRevisions
            .Where(r => r.Id == revisionId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.BillDate, (DateOnly?)null));

        var order = await service.FindEarlierUnpushedAsync(new BillPushOrderRequest([second.Id]));

        var earlier = Assert.Single(order.EarlierBills);
        Assert.Equal((first.Id, (DateOnly?)null), (earlier.BillId, earlier.BillDate));
    }

    [Fact]
    public async Task More_than_two_hundred_bills_is_refused()
    {
        await using var db = CreateDbContext();
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => Service(db).FindEarlierUnpushedAsync(new BillPushOrderRequest(ids)));
    }

    private static Task<BillResponse> Create(BillService service, DateOnly date) =>
        service.CreateDraftAsync(new CreateBillDraftRequest(Payload(date, 100m)));

    private static BillPayloadDto Payload(DateOnly date, decimal grandTotal) =>
        SamplePayload("Customer", grandTotal, date);
}
