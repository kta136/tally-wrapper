using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Shell;
using BillStateDisplay = ShowroomBilling.Desktop.ViewModels.Bills.BillStateDisplay;

namespace ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

/// <summary>
/// What a run came to, shown in place of the form: a headline, why (when it did not go as asked),
/// where the bills are (<c>**bold**</c> words through <c>RichText.Markup</c>), and the first and
/// last bill number when bills were saved. <paramref name="Tone"/> is "ok", "warn" or "err".
/// <paramref name="BillsMayBeSaved"/> decides the buttons: the Bills tab is offered when there may
/// be bills to look at, and otherwise the way back to the form.
/// </summary>
public sealed record BatchResult(
    string Tone,
    string Headline,
    string Reason,
    string Where,
    bool BillsMayBeSaved,
    string? FirstNumber = null,
    string? LastNumber = null)
{
    public bool HasReason => Reason.Length > 0;
    public bool HasNumbers => !string.IsNullOrWhiteSpace(FirstNumber);

    /// <summary>The icon in the round tile: a tick, a warning sign, or a cross.</summary>
    public string Glyph => Tone switch
    {
        "ok" => "",
        "err" => "",
        _ => "",
    };
}

/// <summary>What the Batch Data Scheduler dialog says: the summary beside the form, the question before a batch, and the result.</summary>
public partial class SyntheticBatchViewModel
{
    /// <summary>How far the Invoice tab's rate and the rate typed here may differ before the question warns: a tenth.</summary>
    private const decimal RateWarningShare = 0.10m;

    /// <summary>The word in the state chip: Not started, Running, Done, Stopped or Failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTone))]
    private string runState = "Not started";

    /// <summary>"ok" when the batch has been made, "err" when it failed, "accent" while it runs, else neutral.</summary>
    public string RunTone => RunState switch
    {
        "Done" => "ok",
        "Failed" => "err",
        "Running" => "accent",
        "Stopped" => "warn",
        _ => "neutral",
    };

    /// <summary>What the last run came to; null while the form is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormShown), nameof(IsResultShown), nameof(IsReadyShown), nameof(IsRunningShown), nameof(NextBatchNote), nameof(ShowValidation))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private BatchResult? result;

    /// <summary>What the run was asked to make, kept from when Start was pressed: the form may have moved on since.</summary>
    [ObservableProperty] private IReadOnlyList<ConfirmFact> asked = [];

    /// <summary>The form (and, on the right, what the batch will make) is on screen: before a run and during one.</summary>
    public bool IsFormShown => Result is null;

    /// <summary>The result is on screen in place of the form.</summary>
    public bool IsResultShown => Result is not null;

    /// <summary>The form is on screen and no batch is running: the footer offers Close and "Make bills…".</summary>
    public bool IsReadyShown => Result is null && !IsRunning;

    /// <summary>
    /// A batch is running and has no result yet: the footer offers "Cancel run". (A result is shown
    /// a moment before the run is over, while the times are read again; its buttons then stand alone.)
    /// </summary>
    public bool IsRunningShown => IsRunning && Result is null;

    /// <summary>"Make another batch" and "Back to the form": the form again, as it was left.</summary>
    [RelayCommand]
    private void ShowForm()
    {
        Result = null;
        RunState = "Not started";
        StatusMessage = string.Empty;
    }

    /// <summary>"Open Bills tab": the shell closes the dialog and shows the bills, and the result has been dealt with.</summary>
    [RelayCommand]
    private void OpenBills()
    {
        OpenBillsRequested?.Invoke(this, EventArgs.Empty);
        ShowForm();
    }

    // ── Beside the fields ──────────────────────────────────────────────────────

    /// <summary>In the largest-bill box: the cap, which is a GST rule and cannot be raised.</summary>
    public string MaxBillLimitText => $"GST limit {UiCulture.Rupees(SyntheticBatchPlanLimits.HardMaxBillAmount)}";

    /// <summary>The label of the two items-per-bill boxes, with their limit.</summary>
    public string ItemsPerBillLabel => $"Items per bill, 1 to {SyntheticBatchPlanLimits.MaxItemsPerBillCap}";

    /// <summary>A rate of zero is the one thing the form cannot guess, so the field itself is marked.</summary>
    public bool IsRateMissing => Rate24Kt <= 0m;

    // What the rate box holds, kept as it was typed: the box reads its text back after every key,
    // and a rate worked out again from the number would drop a decimal point just typed.
    private string _rateText = string.Empty;
    private bool _typingRate;

    /// <summary>The 24K rate as the box shows it: empty for no rate (the box then shows "Today's rate"), with or without commas.</summary>
    public string RateText
    {
        get => _rateText;
        set
        {
            _rateText = value ?? string.Empty;
            _typingRate = true;
            try
            {
                Rate24Kt = decimal.TryParse(
                    _rateText.Replace(",", string.Empty).Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
                    ? rate
                    : 0m;
            }
            finally
            {
                _typingRate = false;
            }
        }
    }

    /// <summary>A rate set from elsewhere (the Invoice tab's, when the dialog opens) is shown in the box; one being typed is left as typed.</summary>
    private void ShowRateInBox()
    {
        if (_typingRate) return;
        _rateText = Rate24Kt > 0m ? Rate24Kt.ToString("#,##0.##", UiCulture.Culture) : string.Empty;
        OnPropertyChanged(nameof(RateText));
    }

    /// <summary>The banner across the form: only with the form, and not over a running batch.</summary>
    public bool ShowValidation => HasValidation && IsReadyShown;

    /// <summary>"Saved as": the chip a pending bill carries in the Bills tab.</summary>
    public string SavedAsLabel => BillStateDisplay.Label(BillStates.Pending, editedAfterPush: false);
    public string SavedAsTone => BillStateDisplay.Tone(BillStates.Pending);
    public string SavedAsKind => BillStateDisplay.Kind(BillStates.Pending);

    /// <summary>The line under the times: the earliest and the latest a bill can be dated.</summary>
    public string WindowNote => IsLastBillUnknown
        ? "Could not read when the last bill was made. The server checks the times when the batch starts."
        : EarliestAt is { } first
            ? $"The earliest is {Show(first)}, just after the last bill. The latest is now."
            : "There is no bill yet, so any time up to now can be used.";

    // ── "This batch makes" ─────────────────────────────────────────────────────

    /// <summary>The large line: how many bills the form will make, as a range. A dash while the amounts are not usable.</summary>
    public string BillCountText => CountOfBills(string.Empty);

    /// <summary>"6 to 20 bills", "1 bill"; with a word before the noun, "6 to 20 pending bills".</summary>
    private string CountOfBills(string before)
    {
        if (TotalAmount < SyntheticBatchPlanLimits.MinBillTotal
            || MaxBillAmount < SyntheticBatchPlanLimits.MinBillTotal
            || MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
        {
            return "—";
        }

        var (fewest, most) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
        return fewest == most ? Bills(fewest, before) : $"{fewest} to {Bills(most, before)}";
    }

    /// <summary>Under the count: what one bill can be worth, and why the top is fixed.</summary>
    public string BillRangeNote
        => $"Each between ₹{UiCulture.Rupees(SyntheticBatchPlanLimits.MinBillTotal)} and ₹{UiCulture.Rupees(Math.Clamp(MaxBillAmount, SyntheticBatchPlanLimits.MinBillTotal, SyntheticBatchPlanLimits.HardMaxBillAmount))}. "
           + $"₹{UiCulture.Rupees(SyntheticBatchPlanLimits.HardMaxBillAmount)} is the GST limit and cannot be raised.";

    /// <summary>
    /// What the bills add up to, said as "about" because each bill is priced in steps of weight and
    /// lands a little under its share. It is the total when the bills can hold it, and less when
    /// they cannot: a batch holds at most its bills at the largest bill each. ₹4,00,000 at
    /// ₹1,99,000 a bill can come out as two bills (₹3,98,000) or more (the whole ₹4,00,000), and
    /// ₹99,999 at ₹60,000 a bill is one bill, so ₹60,000 and no more.
    /// </summary>
    public string AddsUpToText
    {
        get
        {
            var total = $"₹{UiCulture.Rupees(TotalAmount)}";
            if (TotalAmount < SyntheticBatchPlanLimits.MinBillTotal
                || MaxBillAmount < SyntheticBatchPlanLimits.MinBillTotal
                || MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
            {
                return total;
            }

            var (fewest, most) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
            var least = Math.Min(TotalAmount, fewest * MaxBillAmount);
            var atMost = Math.Min(TotalAmount, most * MaxBillAmount);
            return least == atMost
                ? $"About ₹{UiCulture.Rupees(atMost)}"
                : $"About ₹{UiCulture.Rupees(least)} to ₹{UiCulture.Rupees(atMost)}";
        }
    }

    /// <summary>The 24K rate as it is sent: to the paisa when one was typed.</summary>
    private string RateShown => $"₹{Rate24Kt.ToString("#,##0.##", UiCulture.Culture)} per gram";

    /// <summary>"21 Nov, 10:43 to 16:45", or with both days when the times are on different days.</summary>
    public string DatedText => StartAt.LocalDateTime.Date == EndAt.LocalDateTime.Date
        ? $"{Show(StartAt)} to {EndAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)}"
        : $"{Show(StartAt)} to {Show(EndAt)}";

    public string ShopHoursText => Hours is { Opens: { } opens, Closes: { } closes }
        ? $"{BatchClock.Format(opens)} to {BatchClock.Format(closes)}"
        : "Any time of day";

    public string ClosedOnText => Hours?.ClosedOn is { } day ? day.ToString() : "Open every day";

    public string GapText => Hours is { GapMinutes: > 1 } set ? $"{set.GapMinutes} minutes apart" : "No gap set";

    /// <summary>
    /// The quiet line when the times are too short to keep the gap: the batch is made all the same,
    /// with bills closer together (owner, 21 November 2026), so this does not stop Start. Empty otherwise.
    /// </summary>
    public string CloserNote
    {
        get
        {
            if (HasValidation || Hours is not { GapMinutes: > 1 } set) return string.Empty;
            var (_, most) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
            var room = BatchOpenMinutes.RoomAtGap(OpenMinutes, set.GapMinutes);
            return room >= most
                ? string.Empty
                : $"These times have room for {Bills(room)} {set.GapMinutes} minutes apart. If the batch makes more, some will be closer together.";
        }
    }

    public bool HasCloserNote => CloserNote.Length > 0;

    /// <summary>On a result: when the next batch can start, once the times have been read again.</summary>
    public string NextBatchNote => Result is { BillsMayBeSaved: true } && !IsLastBillUnknown && EarliestAt is { } next
        ? $"A next batch can be dated from {Show(next)}."
        : string.Empty;

    private static readonly string[] SummaryProperties =
    [
        nameof(WindowNote), nameof(BillCountText), nameof(BillRangeNote), nameof(AddsUpToText), nameof(DatedText),
        nameof(ShopHoursText), nameof(ClosedOnText), nameof(GapText), nameof(CloserNote), nameof(HasCloserNote),
        nameof(NextBatchNote), nameof(ShowValidation),
    ];

    /// <summary>Everything beside the form is worked out from the form, so it is read again whenever the form changes.</summary>
    private void RaiseSummaryChanged()
    {
        foreach (var name in SummaryProperties) OnPropertyChanged(name);
    }

    // ── The question before a batch, and what was asked ────────────────────────

    /// <summary>
    /// The question "Make bills…" asks: the figures to check, since a batch makes numbered bills that
    /// only an admin can delete. A rate far from the Invoice tab's turns it into a warning, with
    /// Cancel as the focused button: an extra or a missing digit prices every bill of the batch wrongly.
    /// </summary>
    private ConfirmRequest BuildConfirmation()
    {
        string? rateNote = null;
        if (InvoiceRate24Kt is { } invoice && invoice > 0m && Math.Abs(Rate24Kt - invoice) / invoice > RateWarningShare)
        {
            var share = Math.Round(Math.Abs(Rate24Kt - invoice) / invoice * 100m, 0, MidpointRounding.AwayFromZero);
            rateNote = $"The 24K rate here, ₹{Rate24Kt.ToString("#,##0.##", UiCulture.Culture)}, is {share:0}% {(Rate24Kt < invoice ? "below" : "above")} the Invoice tab's ₹{invoice.ToString("#,##0.##", UiCulture.Culture)}. Check it before you go on.";
        }

        var hours = Hours is { Opens: not null, Closes: not null }
            ? ShopHoursText + (Hours.ClosedOn is { } day ? $", not on {day}" : string.Empty)
            : Hours?.ClosedOn is { } closed ? $"Any time of day, not on {closed}" : "Any time of day";

        return new ConfirmRequest(
            Title: $"Make {CountOfBills("pending ")}?",
            Message: "They are saved as soon as you say yes, and wait in the Bills tab. Nothing is sent to Tally.",
            ConfirmText: "Make the bills",
            IsWarning: rateNote is not null,
            Note: rateNote,
            Facts:
            [
                new ConfirmFact("Adds up to", AddsUpToText),
                new ConfirmFact("24K rate", RateShown, Marked: rateNote is not null),
                new ConfirmFact("Dated", $"{Show(StartAt)} to {Show(EndAt)}"),
                new ConfirmFact("Shop hours", hours),
                new ConfirmFact("Payment", PaymentMode),
                new ConfirmFact("Items per bill", ItemsRange),
                new ConfirmFact("Karats", SelectedKaratNames),
            ]);
    }

    private string ItemsRange => MinItemsPerBill == MaxItemsPerBill
        ? MinItemsPerBill.ToString(CultureInfo.InvariantCulture)
        : $"{MinItemsPerBill} to {MaxItemsPerBill}";

    private string SelectedKaratNames => string.Join(", ", KaratOptions.Where(k => k.IsSelected).Select(k => k.Label));

    /// <summary>The rows of "What was asked", beside a result.</summary>
    private IReadOnlyList<ConfirmFact> BuildAskedRows() =>
    [
        new ConfirmFact("Total", $"₹{UiCulture.Rupees(TotalAmount)}"),
        new ConfirmFact("Largest single bill", $"₹{UiCulture.Rupees(MaxBillAmount)}"),
        new ConfirmFact("24K rate", RateShown),
        new ConfirmFact("Payment", PaymentMode),
        new ConfirmFact("Items per bill", ItemsRange),
        new ConfirmFact("Dated", DatedText),
        new ConfirmFact("Shop hours", ShopHoursText),
        new ConfirmFact("Closed on", ClosedOnText),
        new ConfirmFact("Bills at least", GapText),
        new ConfirmFact("Karats", SelectedKaratNames),
    ];
}
