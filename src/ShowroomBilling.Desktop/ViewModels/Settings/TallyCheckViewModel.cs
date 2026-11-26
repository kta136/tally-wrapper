using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public enum TallyCheckState
{
    NotRun,
    Comparing,
    Done,
    Failed,
}

/// <summary>
/// Settings → Tally check: compares this year's bills with the sales vouchers in Tally and lists
/// what differs, with a way to open each bill. It only reads. The result is kept while the app is
/// open, and is worked out again only when the operator asks.
/// </summary>
public sealed partial class TallyCheckViewModel : ObservableObject
{
    public const string AllFilter = "all";
    public const string NumberFilter = "number";
    public const string DateOrTotalFilter = "date-or-total";
    public const string MissingFilter = "missing";
    public const string ExtraFilter = "extra";

    /// <summary>
    /// How many rows the table shows at a time. One voucher inserted early in the year can make
    /// every later sale a difference, and each row is laid out whether it is in view or not.
    /// </summary>
    public const int PageSize = 100;

    private readonly ITallyCheckApiClient _api;
    private readonly TimeProvider _clock;
    private IReadOnlyList<TallyCheckRowViewModel> _allRows = [];
    private IReadOnlyList<TallyCheckRowViewModel> _filteredRows = [];

    public TallyCheckViewModel(ITallyCheckApiClient api, TimeProvider? clock = null)
    {
        _api = api;
        _clock = clock ?? TimeProvider.System;
        Filters =
        [
            new TallyCheckFilterViewModel(AllFilter, "All differences"),
            new TallyCheckFilterViewModel(NumberFilter, "Number differs"),
            new TallyCheckFilterViewModel(DateOrTotalFilter, "Date or total differs"),
            new TallyCheckFilterViewModel(MissingFilter, "Missing from Tally"),
            new TallyCheckFilterViewModel(ExtraFilter, "Extra in Tally"),
        ];
        Filters[0].IsSelected = true;
    }

    /// <summary>A bill's "Open bill" was clicked: the shell shows it in Bill details.</summary>
    public event EventHandler<Guid>? OpenBillRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRun), nameof(IsComparing), nameof(IsFailed), nameof(ShowsResult),
        nameof(ShowsAllMatch), nameof(ShowsDifferences), nameof(CompareButtonText), nameof(FailureMarkup),
        nameof(HasResultNote))]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    private TallyCheckState state = TallyCheckState.NotRun;

    /// <summary>The last compare that came back, kept when a later one fails.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResult), nameof(ShowsAllMatch), nameof(ShowsDifferences),
        nameof(LastComparedLine), nameof(LastComparedCounts), nameof(HasLastCompared), nameof(SummaryMarkup),
        nameof(AllMatchMarkup), nameof(FailureMarkup), nameof(NotRunText), nameof(ResultNote), nameof(HasResultNote))]
    private TallyComparisonResponse? result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FailureMarkup))]
    private string failureReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastComparedCounts))]
    private TimeSpan lastDuration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreRows), nameof(MoreRowsText))]
    private IReadOnlyList<TallyCheckRowViewModel> shownRows = [];

    public IReadOnlyList<TallyCheckFilterViewModel> Filters { get; }

    public bool IsNotRun => State == TallyCheckState.NotRun;
    public bool IsComparing => State == TallyCheckState.Comparing;
    public bool IsFailed => State == TallyCheckState.Failed;

    /// <summary>A result is shown after a compare, and under a failed one when there was an earlier result.</summary>
    public bool ShowsResult => Result is not null && State is TallyCheckState.Done or TallyCheckState.Failed;
    public bool ShowsAllMatch => ShowsResult && Result!.Differences.Count == 0;
    public bool ShowsDifferences => ShowsResult && Result!.Differences.Count > 0;

    public string CompareButtonText => IsComparing ? "Comparing…" : "Compare with Tally";

    public bool HasLastCompared => Result is not null;

    public string LastComparedLine => Result is null ? string.Empty : $"Last compared {When(Result.ComparedAtUtc)}";

    public string LastComparedCounts => Result is null
        ? string.Empty
        : $"{Count(Result.BillsCompared, "bill", "bills")} · {Count(Result.VouchersInTally, "voucher", "vouchers")} in Tally · took {Math.Max(1, (int)Math.Round(LastDuration.TotalSeconds))} s";

    public string NotRunText =>
        $"Compare reads every sales voucher of {Result?.FiscalYear ?? InvoiceNumberFormatter.ComputeFiscalYear(_clock.GetUtcNow())} from Tally and checks each against its bill here: "
        + "the voucher's number, date and total. It takes a few seconds and changes nothing.";

    public string SummaryMarkup
    {
        get
        {
            if (Result is null)
            {
                return string.Empty;
            }

            var billRows = Result.Differences.Count(d => !d.ExtraInTally);
            var extras = Result.Differences.Count(d => d.ExtraInTally);
            var parts = new List<string>();
            if (billRows > 0)
            {
                parts.Add($"{Count(billRows, "bill", "bills")} {(billRows == 1 ? "differs" : "differ")} from Tally");
            }
            if (extras > 0)
            {
                parts.Add($"{Count(extras, "voucher", "vouchers")} in Tally {(extras == 1 ? "has" : "have")} no bill");
            }

            var match = Result.Matching == 0 ? "none match" : $"{Result.Matching.ToString("N0", UiCulture.Culture)} match";
            var advice = new List<string>();
            if (billRows > 0)
            {
                // Bill details shows a bill's history; Edit and the Mark actions are on its row in Bills.
                advice.Add("Open a bill to see its history, then fix it from its row in the Bills tab (edit and push, or Mark as Pushed or Mark as Pending after checking Tally).");
            }
            if (extras > 0)
            {
                advice.Add("A voucher with no bill has to be looked at in Tally.");
            }

            return $"**{string.Join(" and ", parts)}; {match}.** {string.Join(" ", advice)}";
        }
    }

    public string AllMatchMarkup => Result switch
    {
        null => string.Empty,
        { BillsCompared: 0, VouchersInTally: 0 } => "**Nothing to compare.** No bill of this year is in Tally yet, and Tally has no sales voucher of the year.",
        { BillsCompared: 1 } => "**The one bill matches Tally.** Its voucher has the same number, date and total, and Tally has no voucher without a bill.",
        _ => $"**All {Result.BillsCompared.ToString("N0", UiCulture.Culture)} bills match Tally.** Every posted bill has its voucher, under the same number, date and total, and Tally has no voucher without a bill.",
    };

    /// <summary>What to keep in mind about the result: pushes made while it ran, bills of another company, and numbers Tally writes with extra zeros.</summary>
    public string ResultNote
    {
        get
        {
            if (Result is null)
            {
                return string.Empty;
            }

            var notes = new List<string>();
            if (Result.PushedWhileComparing)
            {
                notes.Add("A bill was pushed while this compare ran, so it or its voucher may show as a difference that is not one. Compare again before acting on it.");
            }
            if (Result.BillsInOtherCompany > 0)
            {
                notes.Add(Result.BillsInOtherCompany == 1
                    ? "1 bill was pushed to another Tally company than the one Settings name now, and is not compared."
                    : $"{Count(Result.BillsInOtherCompany, "bill", "bills")} were pushed to another Tally company than the one Settings name now, and are not compared.");
            }
            if (Result.MatchingWrittenOtherwise > 0)
            {
                // Tally fills numbers to a width ("…/051" for "…/51"): the same number, so not a row each.
                var n = Result.MatchingWrittenOtherwise;
                notes.Add($"{Count(n, "bill", "bills")} {(n == 1 ? "is" : "are")} written in Tally with zeros in front of the number (051 for 51) and {(n == 1 ? "counts" : "count")} as matching.");
            }

            return string.Join(" ", notes);
        }
    }

    public bool HasResultNote => ShowsResult && ResultNote.Length > 0;

    public string FailureMarkup =>
        $"**Nothing was compared.** {FailureReason}"
        + (Result is null ? string.Empty : $" The last result below is from {When(Result.ComparedAtUtc)}.");

    /// <summary>More rows of the chosen pill than the table shows yet.</summary>
    public bool HasMoreRows => ShownRows.Count < _filteredRows.Count;

    public string MoreRowsText => $"Showing {ShownRows.Count.ToString("N0", UiCulture.Culture)} of {_filteredRows.Count.ToString("N0", UiCulture.Culture)}.";

    private bool CanCompare => State != TallyCheckState.Comparing;

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private async Task CompareAsync(CancellationToken cancellationToken)
    {
        State = TallyCheckState.Comparing;
        var started = _clock.GetTimestamp();
        try
        {
            var compared = await _api.CompareAsync(cancellationToken: cancellationToken);
            LastDuration = _clock.GetElapsedTime(started);
            _allRows = compared.Differences.Select(TallyCheckRowViewModel.From).ToList();
            Result = compared;
            UpdateFilters();
            State = TallyCheckState.Done;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = Result is null ? TallyCheckState.NotRun : TallyCheckState.Done;
        }
        catch (Exception ex)
        {
            FailureReason = ReasonFor(ex);
            State = TallyCheckState.Failed;
        }
    }

    [RelayCommand]
    private void SelectFilter(string key)
    {
        foreach (var filter in Filters)
        {
            filter.IsSelected = filter.Key == key;
        }
        ShowFilteredRows();
    }

    [RelayCommand]
    private void ShowMoreRows() =>
        ShownRows = _filteredRows.Take(ShownRows.Count + PageSize).ToList();

    [RelayCommand]
    private void OpenBill(TallyCheckRowViewModel? row)
    {
        if (row?.BillId is { } id)
        {
            OpenBillRequested?.Invoke(this, id);
        }
    }

    private void UpdateFilters()
    {
        foreach (var filter in Filters)
        {
            filter.Count = _allRows.Count(row => row.IsIn(filter.Key));
        }
        ShowFilteredRows();
    }

    private void ShowFilteredRows()
    {
        var key = Filters.FirstOrDefault(f => f.IsSelected)?.Key ?? AllFilter;
        _filteredRows = _allRows.Where(row => row.IsIn(key)).ToList();
        ShownRows = _filteredRows.Take(PageSize).ToList();
    }

    /// <summary>
    /// The server's reason when it gave one: a 503 here is also how it reports a voucher list it
    /// could not use, which reopening Tally does not cure.
    /// </summary>
    private static string ReasonFor(Exception ex) => ex switch
    {
        ApiException { Detail: { Length: > 0 } detail } => detail,
        ApiException { StatusCode: HttpStatusCode.ServiceUnavailable } =>
            "Tally did not answer: TallyPrime may be closed on the server, or the company not open.",
        ApiException api => api.Message,
        TaskCanceledException => "The server did not answer in time.",
        HttpRequestException => "The server did not answer.",
        _ => ex.Message,
    };

    /// <summary>"today, 21:40", "yesterday, 18:05" or "24 Nov, 09:12", in this PC's time.</summary>
    private string When(DateTimeOffset at)
    {
        var zone = _clock.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone).DateTime);
        var day = DateOnly.FromDateTime(local.DateTime);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return day == today ? $"today, {time}"
            : day == today.AddDays(-1) ? $"yesterday, {time}"
            : $"{local.ToString("d MMM", CultureInfo.InvariantCulture)}, {time}";
    }

    internal static string Count(int n, string one, string many) =>
        $"{n.ToString("N0", UiCulture.Culture)} {(n == 1 ? one : many)}";
}

/// <summary>A pill over the table: which differences are shown, and how many there are.</summary>
public sealed partial class TallyCheckFilterViewModel(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    [ObservableProperty] private int count;
    [ObservableProperty] private bool isSelected;
}

/// <summary>One difference, worded for the table.</summary>
public sealed class TallyCheckRowViewModel
{
    private TallyCheckRowViewModel(TallyComparisonDifference difference) => Difference = difference;

    public TallyComparisonDifference Difference { get; }

    public Guid? BillId => Difference.BillId;
    public bool HasBill => Difference.BillId is not null;

    /// <summary>The chip: what differs.</summary>
    public string Kind { get; private init; } = string.Empty;

    /// <summary>The chip's tone: "err" for a missing or shared voucher, "info" for an extra one, "warn" otherwise.</summary>
    public string Tone { get; private init; } = "warn";

    public string Bill => Difference.BillNumber ?? "—";
    public string BillDate => Difference.BillDate is { } date ? DateText(date) : "—";
    public string BillTotal => Difference.BillTotal is { } total ? Money(total) : "—";

    /// <summary>The bill's date and total, under its number, as the voucher's are under its number.</summary>
    public string BillDetail => Difference.BillId is null
        ? string.Empty
        : Detail(Difference.BillDate, Difference.BillTotal);

    /// <summary>The voucher's number in Tally, "—" when Tally has no voucher for the bill.</summary>
    public string InTallyNumber => Difference.MissingFromTally ? "—" : Difference.TallyNumber ?? "(no number)";

    /// <summary>The voucher's date and total in Tally, under its number.</summary>
    public string InTallyDetail => Difference.MissingFromTally
        ? string.Empty
        : Detail(Difference.TallyDate, Difference.TallyTotal, noTotal: "no total");

    /// <summary>What it means and what to do about it, in a sentence or two.</summary>
    public string Meaning { get; private init; } = string.Empty;

    public bool IsIn(string filter) => filter switch
    {
        TallyCheckViewModel.NumberFilter => Difference.NumberDiffers,
        TallyCheckViewModel.DateOrTotalFilter => Difference.DateDiffers || Difference.TotalDiffers,
        TallyCheckViewModel.MissingFilter => Difference.MissingFromTally,
        TallyCheckViewModel.ExtraFilter => Difference.ExtraInTally,
        _ => true,
    };

    public static TallyCheckRowViewModel From(TallyComparisonDifference d) => new(d)
    {
        Kind = KindOf(d),
        Tone = d.MissingFromTally || d.SharedVoucher ? "err" : d.ExtraInTally ? "info" : "warn",
        Meaning = MeaningOf(d),
    };

    private static string KindOf(TallyComparisonDifference d)
    {
        if (d.MissingFromTally) return "Missing from Tally";
        if (d.ExtraInTally) return "Extra in Tally";
        if (d.SharedVoucher) return "Voucher of two bills";
        var what = new List<string>();
        if (d.NumberDiffers) what.Add("number");
        if (d.DateDiffers) what.Add("date");
        if (d.TotalDiffers) what.Add("total");
        var joined = what.Count switch
        {
            1 => what[0],
            2 => $"{what[0]} and {what[1]}",
            // All three: kept short enough for the chip.
            _ => $"{what[0]}, {what[1]}, {what[2]}",
        };
        var verb = what.Count switch { 1 => " differs", 2 => " differ", _ => string.Empty };
        return char.ToUpperInvariant(joined[0]) + joined[1..] + verb;
    }

    private static string MeaningOf(TallyComparisonDifference d)
    {
        if (d.MissingFromTally)
        {
            // Only the sales voucher type Settings name, dated in this year, was read; and a
            // bill looked for by number may be filed under another. Neither proves it gone.
            return d.TallyMasterId is null
                ? "No voucher with this number among this year's sales vouchers in Tally. It may be filed under another number (an extra voucher in this list may be it): find it in Tally, then Mark as Pushed, or Mark as Pending if it is not there."
                : d.BillState == BillStates.ReconciliationRequired
                    ? "The push's outcome was not known, and the voucher it named is not among this year's sales vouchers in Tally. Check in Tally, then Mark as Pushed or Mark as Pending."
                    : "The voucher its push recorded is not among this year's sales vouchers in Tally: deleted there, or moved to another voucher type or year? Check in Tally, and Mark as Pending and push again only if it is not there.";
        }

        if (d.ExtraInTally)
        {
            return "No bill here accounts for this voucher: a second voucher with that number, or one entered in Tally by hand.";
        }

        var sentences = new List<string>();
        if (d.SharedVoucher)
        {
            sentences.Add("Another bill's push recorded this same voucher, so only one of the two can be in Tally under it. Check in Tally which sale it is.");
        }

        if (d.NumberDiffers)
        {
            sentences.Add(NumberSentence(d.BillNumber, d.TallyNumber));
        }

        var changes = new List<string>();
        if (d.DateDiffers && d.TallyDate is { } tallyDate)
        {
            changes.Add($"dates it {tallyDate.ToString("d MMM", CultureInfo.InvariantCulture)}");
        }
        if (d.TotalDiffers && d.TallyTotal is { } tallyTotal && d.BillTotal is { } billTotal)
        {
            var gap = tallyTotal - billTotal;
            changes.Add($"shows {Money(Math.Abs(gap))} {(gap > 0 ? "more" : "less")}");
        }
        if (changes.Count > 0)
        {
            sentences.Add($"Tally {string.Join(" and ", changes)}. Changed in Tally by hand, or another sale under the same number.");
        }
        if (d.TotalDiffers && d.TallyTotal is null)
        {
            sentences.Add("Tally gave no total for this voucher, so its total could not be checked.");
        }

        return string.Join(" ", sentences);
    }

    private static string NumberSentence(string? billNumber, string? tallyNumber)
    {
        if (tallyNumber is null)
        {
            return "Same voucher, but Tally shows no number for it.";
        }

        // What is seen, not why: the numbering method is Tally's, and not known here.
        return InvoiceNumberFormatter.StepBetween(billNumber, tallyNumber) switch
        {
            0 when tallyNumber.Length == (billNumber?.Length ?? 0) => "The same number, its letters written in another case in Tally.",
            0 => $"The same number, written with {(tallyNumber.Length > (billNumber?.Length ?? 0) ? "more" : "fewer")} zeros in Tally.",
            { } gap => $"Same voucher, filed in Tally {(Math.Abs(gap) == 1 ? "one number" : $"{Math.Abs(gap)} numbers")} {(gap > 0 ? "later" : "earlier")}.",
            null => "Same voucher, filed under another number in Tally.",
        };
    }

    private static string Detail(DateOnly? date, decimal? total, string noTotal = "—") =>
        $"{(date is { } d ? DateText(d) : "—")} · {(total is { } t ? Money(t) : noTotal)}";

    private static string DateText(DateOnly date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal value) =>
        $"₹{(value % 1 == 0 ? UiCulture.Rupees(value) : UiCulture.Amount(value))}";
}
