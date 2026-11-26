using System.Net;
using System.Net.Http;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// Settings → Tally check: what a compare shows, how each difference is worded, the filter pills,
/// what a failed compare says, and that "Open bill" asks the shell for the bill.
/// </summary>
public sealed class TallyCheckViewModelTests
{
    private static readonly Guid BillA = Guid.NewGuid();
    private static readonly Guid BillB = Guid.NewGuid();
    private static readonly Guid BillC = Guid.NewGuid();

    [Fact]
    public async Task A_compare_with_differences_lists_them_and_counts_them_per_pill()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(Sample())));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal(TallyCheckState.Done, vm.State);
        Assert.True(vm.ShowsDifferences);
        Assert.False(vm.ShowsAllMatch);
        Assert.Equal(4, vm.ShownRows.Count);
        Assert.Equal(
            [("all", 4), ("number", 1), ("date-or-total", 1), ("missing", 1), ("extra", 1)],
            vm.Filters.Select(f => (f.Key, f.Count)));
        Assert.Equal(
            "**3 bills differ from Tally and 1 voucher in Tally has no bill; 10 match.** "
            + "Open a bill to see its history, then fix it from its row in the Bills tab (edit and push, or Mark as Pushed or Mark as Pending after checking Tally). "
            + "A voucher with no bill has to be looked at in Tally.",
            vm.SummaryMarkup);
        Assert.StartsWith("Last compared today, ", vm.LastComparedLine);
        Assert.StartsWith("13 bills · 11 vouchers in Tally · took ", vm.LastComparedCounts);
    }

    [Fact]
    public async Task A_pill_shows_only_its_differences()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(Sample())));
        await vm.CompareCommand.ExecuteAsync(null);

        vm.SelectFilterCommand.Execute(TallyCheckViewModel.MissingFilter);

        var row = Assert.Single(vm.ShownRows);
        Assert.Equal(BillC, row.BillId);
        Assert.Equal([false, false, false, true, false], vm.Filters.Select(f => f.IsSelected));
    }

    [Fact]
    public async Task Each_difference_is_worded_for_the_operator()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(Sample())));
        await vm.CompareCommand.ExecuteAsync(null);
        var rows = vm.ShownRows;

        var number = rows.Single(r => r.BillId == BillA);
        Assert.Equal(("Number differs", "warn"), (number.Kind, number.Tone));
        Assert.Equal("Same voucher, filed in Tally one number later.", number.Meaning);
        Assert.Equal(("SR/2026-27/0012", "10 Oct 2026 · ₹1,000"), (number.InTallyNumber, number.InTallyDetail));
        Assert.Equal(("SR/2026-27/0011", "10 Oct 2026 · ₹1,000"), (number.Bill, number.BillDetail));

        var total = rows.Single(r => r.BillId == BillB);
        Assert.Equal("Total differs", total.Kind);
        Assert.Equal("Tally shows ₹212.50 more. Changed in Tally by hand, or another sale under the same number.", total.Meaning);

        var missing = rows.Single(r => r.BillId == BillC);
        Assert.Equal(("Missing from Tally", "err", "—", ""), (missing.Kind, missing.Tone, missing.InTallyNumber, missing.InTallyDetail));
        Assert.StartsWith("The voucher its push recorded is not among this year's sales vouchers in Tally", missing.Meaning);

        var extra = rows.Single(r => r.BillId is null);
        Assert.Equal(("Extra in Tally", "info", false, "—"), (extra.Kind, extra.Tone, extra.HasBill, extra.Bill));
    }

    [Theory]
    [InlineData("SR/2026-27/0011", "SR/2026-27/00011", "The same number, written with more zeros in Tally.")]
    [InlineData("SR/2026-27/0011", "SR/2026-27/11", "The same number, written with fewer zeros in Tally.")]
    [InlineData("SR/2026-27/0011", "sr/2026-27/0011", "The same number, its letters written in another case in Tally.")]
    [InlineData("SR/2026-27/0011", "SR/2026-27/0009", "Same voucher, filed in Tally 2 numbers earlier.")]
    [InlineData("SR/2026-27/0011", "CASH/7", "Same voucher, filed under another number in Tally.")]
    [InlineData("SR/11/26-27", "SR/011/26-27", "The same number, written with more zeros in Tally.")]
    [InlineData("SR/11/26-27", "SR/12/26-27", "Same voucher, filed in Tally one number later.")]
    [InlineData("SR/2026-27/0011", null, "Same voucher, but Tally shows no number for it.")]
    public void A_number_difference_says_how_the_numbers_differ(string billNumber, string? tallyNumber, string meaning)
    {
        var row = TallyCheckRowViewModel.From(Difference(BillA, billNumber, tallyNumber: tallyNumber, numberDiffers: true));

        Assert.Equal(meaning, row.Meaning);
    }

    [Fact]
    public void Several_things_differing_are_named_together()
    {
        var row = TallyCheckRowViewModel.From(Difference(
            BillA, "SR/2026-27/0011", tallyNumber: "SR/2026-27/0012", tallyDate: new DateOnly(2026, 10, 9), tallyTotal: 900m,
            numberDiffers: true, dateDiffers: true, totalDiffers: true));

        Assert.Equal("Number, date, total", row.Kind);
        Assert.Equal(
            "Same voucher, filed in Tally one number later. "
            + "Tally dates it 9 Oct and shows ₹100 less. Changed in Tally by hand, or another sale under the same number.",
            row.Meaning);
    }

    [Fact]
    public void A_bill_looked_for_by_number_is_not_said_to_be_gone_from_Tally()
    {
        // No push recorded an id: Tally may hold the voucher under another number.
        var row = TallyCheckRowViewModel.From(Difference(BillA, "SR/2026-27/0011", missing: true) with { TallyMasterId = null });

        Assert.StartsWith("No voucher with this number among this year's sales vouchers in Tally. It may be filed under another number", row.Meaning);
    }

    [Fact]
    public void A_voucher_two_bills_claim_and_a_total_Tally_did_not_give_are_said_so()
    {
        var shared = TallyCheckRowViewModel.From(Difference(BillA, "SR/2026-27/0011", tallyNumber: "SR/2026-27/0011") with { SharedVoucher = true });
        Assert.Equal(("Voucher of two bills", "err"), (shared.Kind, shared.Tone));
        Assert.StartsWith("Another bill's push recorded this same voucher", shared.Meaning);

        var noTotal = TallyCheckRowViewModel.From(
            Difference(BillA, "SR/2026-27/0011", tallyNumber: "SR/2026-27/0011", totalDiffers: true) with { TallyTotal = null });
        Assert.Equal("Total differs", noTotal.Kind);
        Assert.Equal("Tally gave no total for this voucher, so its total could not be checked.", noTotal.Meaning);
        Assert.Equal("10 Oct 2026 · no total", noTotal.InTallyDetail);
    }

    [Fact]
    public async Task Bills_of_another_company_and_a_push_during_the_compare_are_noted()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(Sample() with { BillsInOtherCompany = 2, PushedWhileComparing = true })));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.True(vm.HasResultNote);
        Assert.Equal(
            "A bill was pushed while this compare ran, so it or its voucher may show as a difference that is not one. Compare again before acting on it. "
            + "2 bills were pushed to another Tally company than the one Settings name now, and are not compared.",
            vm.ResultNote);
    }

    [Theory]
    [InlineData(1, "1 bill is written in Tally with zeros in front of the number (051 for 51) and counts as matching.")]
    [InlineData(81, "81 bills are written in Tally with zeros in front of the number (051 for 51) and count as matching.")]
    public async Task Numbers_Tally_writes_with_extra_zeros_are_one_line_not_a_row_each(int count, string note)
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(
            new TallyComparisonResponse("2026-27", DateTimeOffset.UtcNow, 100, 100, 100, [], MatchingWrittenOtherwise: count))));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.True(vm.ShowsAllMatch);
        Assert.True(vm.HasResultNote);
        Assert.Equal(note, vm.ResultNote);
    }

    [Fact]
    public async Task A_long_list_is_shown_a_page_at_a_time()
    {
        var many = Enumerable.Range(1, 250)
            .Select(i => Difference(Guid.NewGuid(), $"SR/2026-27/{i:D4}", tallyNumber: $"SR/2026-27/{i + 1:D4}", numberDiffers: true))
            .ToList();
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(Sample() with { Differences = many })));
        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal((100, true, "Showing 100 of 250."), (vm.ShownRows.Count, vm.HasMoreRows, vm.MoreRowsText));
        vm.ShowMoreRowsCommand.Execute(null);
        vm.ShowMoreRowsCommand.Execute(null);
        Assert.Equal((250, false), (vm.ShownRows.Count, vm.HasMoreRows));
        Assert.Equal(250, vm.Filters[0].Count);
    }

    [Fact]
    public async Task When_everything_matches_it_says_so()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(new TallyComparisonResponse("2026-27", DateTimeOffset.UtcNow, 12, 12, 12, []))));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.True(vm.ShowsAllMatch);
        Assert.False(vm.ShowsDifferences);
        Assert.StartsWith("**All 12 bills match Tally.**", vm.AllMatchMarkup);
    }

    [Fact]
    public async Task Tally_not_answering_compares_nothing_and_keeps_the_last_result_below()
    {
        var api = new ScriptedApi(
            Answer(Sample()),
            () => Task.FromException<TallyComparisonResponse>(
                new ApiException(HttpStatusCode.ServiceUnavailable, "Tally unavailable", "Tally could not be asked.", null)));
        var vm = new TallyCheckViewModel(api);
        await vm.CompareCommand.ExecuteAsync(null);

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal(TallyCheckState.Failed, vm.State);
        Assert.True(vm.ShowsDifferences);
        // The server's own reason is kept: a 503 is also how it reports a list it could not use.
        Assert.StartsWith(
            "**Nothing was compared.** Tally could not be asked. The last result below is from today, ",
            vm.FailureMarkup);
    }

    [Fact]
    public async Task A_503_without_a_reason_says_Tally_did_not_answer()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(() => Task.FromException<TallyComparisonResponse>(
            new ApiException(HttpStatusCode.ServiceUnavailable, null, null, null))));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal(
            "**Nothing was compared.** Tally did not answer: TallyPrime may be closed on the server, or the company not open.",
            vm.FailureMarkup);
    }

    [Fact]
    public async Task No_server_compares_nothing()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(() => Task.FromException<TallyComparisonResponse>(new HttpRequestException("refused"))));

        await vm.CompareCommand.ExecuteAsync(null);

        Assert.Equal(TallyCheckState.Failed, vm.State);
        Assert.False(vm.ShowsResult);
        Assert.Equal("**Nothing was compared.** The server did not answer.", vm.FailureMarkup);
    }

    [Fact]
    public async Task While_comparing_the_button_waits()
    {
        var answer = new TaskCompletionSource<TallyComparisonResponse>();
        var vm = new TallyCheckViewModel(new ScriptedApi(() => answer.Task));
        Assert.Equal(("Compare with Tally", true), (vm.CompareButtonText, vm.IsNotRun));

        var comparing = vm.CompareCommand.ExecuteAsync(null);

        Assert.True(vm.IsComparing);
        Assert.Equal("Comparing…", vm.CompareButtonText);
        Assert.False(vm.CompareCommand.CanExecute(null));
        answer.SetResult(Sample());
        await comparing;
        Assert.True(vm.CompareCommand.CanExecute(null));
    }

    [Fact]
    public async Task Open_bill_asks_for_the_rows_bill()
    {
        var vm = new TallyCheckViewModel(new ScriptedApi(Answer(Sample())));
        await vm.CompareCommand.ExecuteAsync(null);
        var asked = new List<Guid>();
        vm.OpenBillRequested += (_, id) => asked.Add(id);

        vm.OpenBillCommand.Execute(vm.ShownRows.Single(r => r.BillId == BillB));
        vm.OpenBillCommand.Execute(vm.ShownRows.Single(r => r.BillId is null));

        Assert.Equal([BillB], asked);
    }

    internal static TallyComparisonResponse Sample() => new(
        "2026-27",
        DateTimeOffset.UtcNow,
        BillsCompared: 13,
        VouchersInTally: 11,
        Matching: 10,
        [
            Difference(BillA, "SR/2026-27/0011", tallyNumber: "SR/2026-27/0012", numberDiffers: true),
            Difference(BillB, "SR/2026-27/0013", tallyNumber: "SR/2026-27/0013", tallyTotal: 1212.50m, totalDiffers: true),
            Difference(BillC, "SR/2026-27/0014", missing: true),
            new TallyComparisonDifference(
                null, null, null, null, null, "999", "SR/2026-27/0012", new DateOnly(2026, 10, 10), 500m,
                NumberDiffers: false, DateDiffers: false, TotalDiffers: false, MissingFromTally: false, ExtraInTally: true),
        ]);

    private static TallyComparisonDifference Difference(
        Guid billId,
        string billNumber,
        string? tallyNumber = null,
        DateOnly? tallyDate = null,
        decimal? tallyTotal = null,
        bool numberDiffers = false,
        bool dateDiffers = false,
        bool totalDiffers = false,
        bool missing = false) => new(
        billId, billNumber, BillStates.Posted, new DateOnly(2026, 10, 10), 1000m,
        "101",
        missing ? null : tallyNumber,
        missing ? null : tallyDate ?? new DateOnly(2026, 10, 10),
        missing ? null : tallyTotal ?? 1000m,
        numberDiffers, dateDiffers, totalDiffers, missing, ExtraInTally: false);

    private static Func<Task<TallyComparisonResponse>> Answer(TallyComparisonResponse response) => () => Task.FromResult(response);

    /// <summary>Answers each compare with the next of its answers, and the last one after that.</summary>
    internal sealed class ScriptedApi(params Func<Task<TallyComparisonResponse>>[] answers) : ITallyCheckApiClient
    {
        private int _calls;

        public Task<TallyComparisonResponse> CompareAsync(string? fiscalYear = null, CancellationToken cancellationToken = default)
            => answers[Math.Min(_calls++, answers.Length - 1)]();
    }
}
