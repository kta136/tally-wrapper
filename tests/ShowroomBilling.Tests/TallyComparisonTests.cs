using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Tally;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

/// <summary>
/// Settings → Tally check: the year's bills set against the sales vouchers
/// Tally lists. Bills are made and pushed through <see cref="BillService"/>, so
/// what is compared is what a push really records.
/// </summary>
public sealed class TallyComparisonTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    private const string Company = "Test Company";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.Date);

    [Fact]
    public async Task A_pushed_bill_whose_voucher_shows_it_as_pushed_matches()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));

        Assert.Equal(1, result.BillsCompared);
        Assert.Equal(1, result.VouchersInTally);
        Assert.Equal(1, result.Matching);
        Assert.Empty(result.Differences);
        Assert.Equal(bill.FiscalYear, result.FiscalYear);
    }

    [Fact]
    public async Task A_voucher_Tally_filed_under_another_number_is_listed_with_both_numbers()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", "OTHER/9", 100m));

        var difference = Assert.Single(result.Differences);
        Assert.True(difference.NumberDiffers);
        Assert.False(difference.DateDiffers || difference.TotalDiffers || difference.MissingFromTally || difference.ExtraInTally);
        Assert.Equal(bill.Id, difference.BillId);
        Assert.Equal(bill.InvoiceNumber, difference.BillNumber);
        Assert.Equal("OTHER/9", difference.TallyNumber);
        Assert.Equal("101", difference.TallyMasterId);
        Assert.Equal(0, result.Matching);
    }

    [Fact]
    public async Task A_voucher_with_another_date_and_total_is_listed()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 150m, Today.AddDays(-1)));

        var difference = Assert.Single(result.Differences);
        Assert.True(difference.DateDiffers);
        Assert.True(difference.TotalDiffers);
        Assert.False(difference.NumberDiffers);
        Assert.Equal(100m, difference.BillTotal);
        Assert.Equal(150m, difference.TallyTotal);
        Assert.Equal(Today, difference.BillDate);
        Assert.Equal(Today.AddDays(-1), difference.TallyDate);
    }

    [Fact]
    public async Task A_pushed_bill_whose_voucher_Tally_no_longer_has_is_missing()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(bill.FiscalYear);

        var difference = Assert.Single(result.Differences);
        Assert.True(difference.MissingFromTally);
        Assert.Equal(bill.Id, difference.BillId);
        Assert.Null(difference.TallyNumber);
        Assert.Equal(0, result.VouchersInTally);
    }

    [Fact]
    public async Task A_voucher_no_bill_accounts_for_is_extra()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(
            bill.FiscalYear,
            Voucher("101", bill.InvoiceNumber, 100m),
            Voucher("999", "BY-HAND/1", 40m));

        var difference = Assert.Single(result.Differences);
        Assert.True(difference.ExtraInTally);
        Assert.Null(difference.BillId);
        Assert.Equal("999", difference.TallyMasterId);
        Assert.Equal("BY-HAND/1", difference.TallyNumber);
        Assert.Equal(1, result.Matching);
    }

    [Fact]
    public async Task A_bill_marked_as_pushed_by_hand_is_found_by_its_number_even_with_zeros_in_front()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        // Tally's voucher type fills numbers with one more zero than the bill's.
        var padded = Regex.Replace(bill.InvoiceNumber!, @"(\d+)(\D*)$", "0$1$2");

        var result = await CompareAsync(bill.FiscalYear, Voucher("4521", padded, 100m));

        // Found, and the same number: it matches, and is counted as written otherwise.
        Assert.Empty(result.Differences);
        Assert.Equal(1, result.Matching);
        Assert.Equal(1, result.MatchingWrittenOtherwise);
    }

    [Fact]
    public async Task A_voucher_number_Tally_writes_with_extra_zeros_or_in_other_case_is_the_same_number()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        var padded = Regex.Replace(bill.InvoiceNumber!, @"(\d+)(\D*)$", "0$1$2");

        var zeros = await CompareAsync(bill.FiscalYear, Voucher("101", padded, 100m));
        var same = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));
        // A number with letters in it, which Tally writes in lower case.
        await db.Bills.Where(b => b.Id == bill.Id).ExecuteUpdateAsync(b => b.SetProperty(x => x.InvoiceNumber, "SR/26-27/7"));
        var lowerCase = await CompareAsync(bill.FiscalYear, Voucher("101", "sr/26-27/7", 100m));

        Assert.Equal((1, 1, 0), (zeros.Matching, zeros.MatchingWrittenOtherwise, zeros.Differences.Count));
        Assert.Equal((1, 1, 0), (lowerCase.Matching, lowerCase.MatchingWrittenOtherwise, lowerCase.Differences.Count));
        Assert.Equal((1, 0, 0), (same.Matching, same.MatchingWrittenOtherwise, same.Differences.Count));
    }

    [Fact]
    public async Task Extra_zeros_beside_a_real_difference_list_only_the_real_one()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        var padded = Regex.Replace(bill.InvoiceNumber!, @"(\d+)(\D*)$", "0$1$2");

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", padded, 150m));

        var difference = Assert.Single(result.Differences);
        Assert.True(difference.TotalDiffers);
        Assert.False(difference.NumberDiffers);
        Assert.Equal(padded, difference.TallyNumber);
        Assert.Equal((0, 0), (result.Matching, result.MatchingWrittenOtherwise));
    }

    [Fact]
    public async Task A_bill_whose_revision_is_dated_after_the_push_is_compared_as_it_is_now()
    {
        // Bills brought over from V1 can have their saved revision dated a little after the push
        // that put them in Tally; the bill has not been edited since, so it is held as it is.
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        await db.BillRevisions.Where(r => r.BillId == bill.Id)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.CreatedAtUtc, x => x.CreatedAtUtc.AddHours(1)));

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));

        Assert.Equal(1, result.Matching);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task An_edited_bill_whose_revisions_are_all_dated_after_the_push_is_held_as_first_saved()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        await Service(db).UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(Payload(150m)), callerIsAdmin: true);
        await db.BillRevisions.Where(r => r.BillId == bill.Id)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.CreatedAtUtc, x => x.CreatedAtUtc.AddHours(1)));

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));

        // Tally holds the bill as it was before the edit: its first revision.
        Assert.Equal(1, result.Matching);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task A_bill_never_pushed_is_not_compared()
    {
        await using var db = CreateDbContext();
        var bill = await Service(db).CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));

        var result = await CompareAsync(bill.FiscalYear);

        Assert.Equal(0, result.BillsCompared);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task An_edit_not_yet_pushed_is_not_a_difference()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        await Service(db).UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(Payload(150m)), callerIsAdmin: true);

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));

        Assert.Equal(1, result.BillsCompared);
        Assert.Equal(1, result.Matching);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task A_bill_waiting_for_reconciliation_is_compared_by_its_number()
    {
        await using var db = CreateDbContext();
        var poster = new BillServiceTests.FakeTallyPoster([TallyPostResponse.Unknown("TIMEOUT", "Tally did not answer.")]);
        var service = Service(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));
        Assert.Equal(BillStates.ReconciliationRequired, pushed.State);

        var result = await CompareAsync(bill.FiscalYear, Voucher("4521", bill.InvoiceNumber, 100m));

        Assert.Equal(1, result.BillsCompared);
        Assert.Equal(1, result.Matching);
    }

    [Fact]
    public async Task After_Mark_as_Pending_the_bill_is_not_compared_and_its_old_voucher_is_extra()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        await Service(db).MarkPendingAsync(bill.Id, new MarkBillStateRequest("not in Tally"));

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));

        Assert.Equal(0, result.BillsCompared);
        Assert.True(Assert.Single(result.Differences).ExtraInTally);
    }

    [Fact]
    public async Task Tally_not_answering_compares_nothing()
    {
        await using var db = CreateDbContext();
        var lookup = new ListedVouchers { Failure = new HttpRequestException("No connection could be made") };

        var ex = await Assert.ThrowsAsync<TallyPreflightUnavailableException>(
            () => new TallyComparison(db, lookup).CompareAsync("2026-27"));

        Assert.Equal("Tally could not be asked for its sales vouchers of 2026-27: No connection could be made", ex.Message);
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("26-27")]
    [InlineData("2026-27; drop")]
    [InlineData("2026-99")]
    [InlineData("9999-00")]
    [InlineData("2026-\u0968\u096D")]
    public async Task A_fiscal_year_not_written_like_2026_27_is_refused(string fiscalYear)
    {
        await using var db = CreateDbContext();
        var lookup = new ListedVouchers();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => new TallyComparison(db, lookup).CompareAsync(fiscalYear));
        Assert.Empty(lookup.Asked);
    }

    [Fact]
    public async Task A_bill_pushed_to_another_company_is_counted_apart_and_its_id_not_looked_up()
    {
        await using var db = CreateDbContext();
        var poster = new BillServiceTests.FakeTallyPoster(tallyMasterId: "101") { Company = "Company A" };
        var service = Service(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));

        // Settings now name Company B, where id 101 is somebody else's sale.
        await using var check = CreateDbContext();
        var result = await new TallyComparison(check, new ListedVouchers(Voucher("101", "OTHER/1", 50m)) { Company = "Company B" })
            .CompareAsync(bill.FiscalYear);

        Assert.Equal(1, result.BillsInOtherCompany);
        Assert.Equal(0, result.BillsCompared);
        Assert.True(Assert.Single(result.Differences).ExtraInTally);
    }

    [Fact]
    public async Task A_bill_waiting_for_reconciliation_is_compared_with_what_its_unsettled_push_sent()
    {
        await using var db = CreateDbContext();
        var poster = new BillServiceTests.FakeTallyPoster(
        [
            TallyPostResponse.Posted("k1", tallyMasterId: "101"),
            TallyPostResponse.Unknown("TIMEOUT", "Tally did not answer."),
        ]);
        var service = new BillService(db, new NumberingService(db), poster, tallyVoucherLookup: new ListedVouchers(Voucher("101", null, 100m)));
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        await service.UpdateDraftAsync(bill.Id, new UpdateBillDraftRequest(Payload(150m)), callerIsAdmin: true);
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("alter"));
        Assert.Equal(BillStates.ReconciliationRequired, pushed.State);

        // The alter landed: Tally shows the edited total, which is no difference.
        var landed = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 150m));
        Assert.Equal(1, landed.Matching);

        // It did not: Tally still shows the old total.
        var lost = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));
        Assert.True(Assert.Single(lost.Differences).TotalDiffers);
    }

    [Fact]
    public async Task A_lost_repost_is_looked_for_by_number_not_by_the_voucher_an_earlier_push_made()
    {
        await using var db = CreateDbContext();
        var poster = new BillServiceTests.FakeTallyPoster(
        [
            TallyPostResponse.Posted("k1", tallyMasterId: "101"),
            TallyPostResponse.Unknown("TIMEOUT", "Tally did not answer."),
        ]);
        var service = Service(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        await service.PushAsync(bill.Id, new PushBillRequest("first"));
        // 101 was deleted in Tally and the bill reposted; Tally made 202 but the answer was lost.
        await service.RepostAsync(bill.Id, new RepostBillRequest("again", "voucher deleted in Tally"));

        var result = await CompareAsync(bill.FiscalYear, Voucher("202", bill.InvoiceNumber, 100m));

        Assert.Equal(1, result.Matching);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public async Task A_number_written_exactly_wins_over_a_padded_one_when_date_and_total_agree()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        var padded = Regex.Replace(bill.InvoiceNumber!, @"(\d+)(\D*)$", "0$1$2");

        // The padded one first: which is taken must not depend on Tally's order.
        var result = await CompareAsync(bill.FiscalYear, Voucher("4521", padded, 100m), Voucher("4522", bill.InvoiceNumber, 100m));

        Assert.Equal(1, result.Matching);
        Assert.Equal("4521", Assert.Single(result.Differences).TallyMasterId);
        Assert.True(result.Differences[0].ExtraInTally);
    }

    [Fact]
    public async Task A_push_whose_outcome_became_unknown_while_comparing_is_said_so()
    {
        await using var db = CreateDbContext();
        var poster = new BillServiceTests.FakeTallyPoster([TallyPostResponse.Unknown("TIMEOUT", "Tally did not answer.")]);
        var service = Service(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        var lookup = new ListedVouchers();
        var comparison = new TallyComparison(db, lookup);

        // The push settles between the read of Tally and the read of the bills.
        lookup.WhenListed = () => service.PushAsync(bill.Id, new PushBillRequest("during")).GetAwaiter().GetResult();
        var result = await comparison.CompareAsync(bill.FiscalYear);

        Assert.True(result.PushedWhileComparing);
    }

    [Fact]
    public async Task A_total_Tally_did_not_give_is_not_counted_as_matching()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(bill.FiscalYear, new TallyVoucherRef("101", Today, null, Company, bill.InvoiceNumber));

        var difference = Assert.Single(result.Differences);
        Assert.True(difference.TotalDiffers);
        Assert.Null(difference.TallyTotal);
        Assert.Equal(0, result.Matching);
    }

    [Fact]
    public async Task Two_bills_whose_pushes_recorded_one_voucher_are_both_listed()
    {
        await using var db = CreateDbContext();
        var first = await PushedAsync(db, "101", 100m);
        var second = await PushedAsync(db, "101", 100m);

        var result = await CompareAsync(first.FiscalYear, Voucher("101", first.InvoiceNumber, 100m));

        Assert.Equal(2, result.Differences.Count);
        Assert.All(result.Differences, d => Assert.True(d.SharedVoucher));
        Assert.Equal([first.Id, second.Id], result.Differences.Select(d => d.BillId!.Value).Order());
        Assert.Equal(0, result.Matching);
    }

    [Fact]
    public async Task A_bill_being_pushed_while_comparing_is_said_so()
    {
        await using var db = CreateDbContext();
        var bill = await PushedAsync(db, "101", 100m);
        var other = await Service(db).CreateDraftAsync(new CreateBillDraftRequest(Payload(50m)));
        await db.Bills.Where(b => b.Id == other.Id).ExecuteUpdateAsync(b => b.SetProperty(x => x.State, BillStates.Posting));

        var result = await CompareAsync(bill.FiscalYear, Voucher("101", bill.InvoiceNumber, 100m));

        Assert.True(result.PushedWhileComparing);
    }

    [Fact]
    public async Task A_number_with_its_year_after_the_core_is_still_found_with_zeros_in_front()
    {
        await using var db = CreateDbContext();
        var service = Service(db);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(100m)));
        await service.MarkPostedAsync(bill.Id, new MarkBillStateRequest("entered in Tally by hand"));
        await db.Bills.Where(b => b.Id == bill.Id).ExecuteUpdateAsync(b => b.SetProperty(x => x.InvoiceNumber, "SR/51/26-27"));

        var result = await CompareAsync(bill.FiscalYear, Voucher("4521", "SR/051/26-27", 100m));

        // Found by number although the zeros are not on its last digits, and the same number.
        Assert.Empty(result.Differences);
        Assert.Equal((1, 1), (result.Matching, result.MatchingWrittenOtherwise));
    }

    [Fact]
    public async Task With_no_year_named_the_current_one_is_asked_for()
    {
        await using var db = CreateDbContext();
        var lookup = new ListedVouchers();

        var result = await new TallyComparison(db, lookup).CompareAsync(null);

        Assert.Equal([result.FiscalYear], lookup.Asked);
        Assert.Matches(@"^\d{4}-\d{2}$", result.FiscalYear);
    }

    private static async Task<BillResponse> PushedAsync(ShowroomBillingDbContext db, string masterId, decimal total)
    {
        var poster = new BillServiceTests.FakeTallyPoster(tallyMasterId: masterId);
        var service = Service(db, poster);
        var bill = await service.CreateDraftAsync(new CreateBillDraftRequest(Payload(total)));
        var pushed = await service.PushAsync(bill.Id, new PushBillRequest("first"));
        Assert.Equal(BillStates.Posted, pushed.State);
        return pushed;
    }

    /// <summary>Compares on a context of its own, as a request of its own would.</summary>
    private async Task<TallyComparisonResponse> CompareAsync(string? fiscalYear, params TallyVoucherRef[] vouchers)
    {
        await using var db = CreateDbContext();
        return await new TallyComparison(db, new ListedVouchers(vouchers)).CompareAsync(fiscalYear);
    }

    private static TallyVoucherRef Voucher(string masterId, string? number, decimal total, DateOnly? date = null) =>
        new(masterId, date ?? Today, total, Company, number);

    // No voucher lookup: a create needs none, and the read-back after a push
    // is not what these tests are about.
    private static BillService Service(ShowroomBillingDbContext db, ITallyPoster? poster = null) =>
        new(db, new NumberingService(db), poster ?? new BillServiceTests.FakeTallyPoster());

    private static BillPayloadDto Payload(decimal grandTotal) =>
        new(
            PartyName: "Customer",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: Today,
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", grandTotal / 10m, grandTotal, "22K", null)
            ],
            Totals: new BillTotalsDto(grandTotal, 0m, 0m, 0m, grandTotal),
            Notes: null);

    /// <summary>The year's sales vouchers as Tally lists them.</summary>
    private sealed class ListedVouchers(params TallyVoucherRef[] vouchers) : ITallyVoucherLookup
    {
        public Exception? Failure { get; init; }
        /// <summary>The company Settings name now, which the list is read in.</summary>
        public string? Company { get; init; } = TallyComparisonTests.Company;
        public List<string> Asked { get; } = [];
        /// <summary>Runs after Tally has been read and before the answer is used.</summary>
        public Action? WhenListed { get; set; }

        public Task<TallyVoucherList> ListSalesVouchersAsync(string fiscalYear, CancellationToken cancellationToken = default)
        {
            Asked.Add(fiscalYear);
            WhenListed?.Invoke();
            return Failure is null
                ? Task.FromResult(new TallyVoucherList(new TallyVoucherDestination(Company, "Sales"), vouchers))
                : Task.FromException<TallyVoucherList>(Failure);
        }

        public Task<IReadOnlyList<TallyVoucherRef>> FindByNumberAsync(string voucherNumber, string fiscalYear, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TallyVoucherRef?> FindByMasterIdAsync(string masterId, string fiscalYear, CancellationToken cancellationToken = default) =>
            Task.FromResult(vouchers.FirstOrDefault(v => v.MasterId == masterId));

        public Task<TallyVoucherDestination> DestinationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TallyVoucherDestination(Company, "Sales"));
    }
}
