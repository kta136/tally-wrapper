using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

/// <summary>
/// The Batch Data Scheduler dialog: a form on the left, what the batch will make on the right, and,
/// once a run has ended, its result in place of the form. What the dialog says beside its fields is
/// in the Display part of this class.
/// </summary>
public partial class SyntheticBatchViewModel : ObservableObject
{
    private readonly ISyntheticBatchApiClient _batchApi;
    private readonly IDialogService _dialogs;
    private readonly AdminTokenStore? _adminTokens;
    private readonly SettingsViewModel? _settings;
    private readonly TimeProvider _time;

    /// <summary>
    /// Fires when a batch has, or may have, saved bills, so the Bills tab can load again: a batch
    /// made whole, one that stopped part-way, and one whose outcome could not be read.
    /// </summary>
    public event EventHandler? BillsSaved;

    /// <summary>Fires when the operator asks for the Bills tab from a result; the shell closes the dialog and shows it.</summary>
    public event EventHandler? OpenBillsRequested;

    public SyntheticBatchViewModel(
        ISyntheticBatchApiClient batchApi,
        IDialogService dialogs,
        AdminTokenStore? adminTokens,
        SettingsViewModel? settings,
        TimeProvider? time = null)
    {
        _batchApi = batchApi;
        _dialogs = dialogs;
        _adminTokens = adminTokens;
        _settings = settings;
        _time = time ?? TimeProvider.System;

        KaratOptions = new ObservableCollection<SelectableKarat>();
        PaymentOptions = new ObservableCollection<string> { Contracts.Bills.PaymentMode.Cash, Contracts.Bills.PaymentMode.CreditDebit };

        // Settings → Admin edits the shop hours; the dialog follows what was last read or saved.
        HoursEditor = new BatchHoursViewModel(batchApi, dialogs, adminTokens);
        HoursEditor.Changed += (_, saved) => Hours = saved;
        if (_adminTokens is not null)
        {
            // The card shows only while admin is unlocked, so that is when it is filled in.
            _adminTokens.Changed += unlock =>
            {
                if (unlock is not null) _ = HoursEditor.LoadAsync();
            };
        }

        // Until the last bill's time is read (PrepareWindowAsync): from the start of today to now.
        var now = Now;
        StartAt = new DateTimeOffset(now.Date, now.Offset);
        EndAt = FloorToMinute(now);

        if (_settings is not null)
        {
            _settings.PropertyChanged += OnSettingsPropertyChanged;
            RefreshKaratOptions();
        }

        RecomputeValidation();
    }

    private CancellationTokenSource? _cts;

    public ObservableCollection<SelectableKarat> KaratOptions { get; }
    public ObservableCollection<string> PaymentOptions { get; }

    /// <summary>"Shop hours for a batch" in Settings → Admin.</summary>
    public BatchHoursViewModel HoursEditor { get; }

    [ObservableProperty] private long totalAmount = 1_000_000;
    [ObservableProperty] private long maxBillAmount = SyntheticBatchPlanLimits.HardMaxBillAmount;
    // No built-in rate: it is taken from the Invoice tab when the dialog
    // opens, or typed. A stale default would price a whole batch wrongly.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRateMissing))]
    private decimal rate24Kt;

    [ObservableProperty] private string paymentMode = Contracts.Bills.PaymentMode.Cash;
    [ObservableProperty] private int minItemsPerBill = 1;
    [ObservableProperty] private int maxItemsPerBill = 3;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FromDate), nameof(FromTime))]
    private DateTimeOffset startAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToDate), nameof(ToTime))]
    private DateTimeOffset endAt;

    /// <summary>
    /// The first minute a batch may date a bill, in local time, as the server works it out: just after
    /// the last bill, where a bill dated an earlier day than it was saved counts as the end of that day.
    /// Null when there is no bill or it could not be read.
    /// </summary>
    [ObservableProperty] private DateTimeOffset? earliestAt;

    /// <summary>
    /// The shop hours a batch keeps to, as the server holds them (Settings → Admin). Null until they
    /// are read, and from a server that has none: bills can then be dated at any time of day.
    /// </summary>
    [ObservableProperty] private BatchHoursSettings? hours;

    /// <summary>
    /// The Invoice tab's 24K rate when the dialog was opened, if it has one. A rate typed here that
    /// is far from it is most likely a slip, and the question before a batch says so.
    /// </summary>
    public decimal? InvoiceRate24Kt { get; set; }

    private DateTimeOffset Now => _time.GetLocalNow();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(LastBillToNowCommand))]
    [NotifyPropertyChangedFor(nameof(IsFormShown), nameof(IsReadyShown), nameof(IsRunningShown), nameof(ShowValidation))]
    private bool isRunning;

    /// <summary>The one line under the bar while a batch runs.</summary>
    [ObservableProperty] private string statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidation))]
    private string? validationMessage;

    public bool HasValidation => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasKaratOptions => KaratOptions.Count > 0;

    partial void OnTotalAmountChanged(long value) => RecomputeValidation();
    partial void OnMaxBillAmountChanged(long value) => RecomputeValidation();
    partial void OnRate24KtChanged(decimal value)
    {
        ShowRateInBox();
        RecomputeValidation();
    }
    partial void OnMinItemsPerBillChanged(int value) => RecomputeValidation();
    partial void OnMaxItemsPerBillChanged(int value) => RecomputeValidation();
    partial void OnStartAtChanged(DateTimeOffset value) => RecomputeValidation();
    partial void OnEndAtChanged(DateTimeOffset value) => RecomputeValidation();
    partial void OnEarliestAtChanged(DateTimeOffset? value) => RecomputeValidation();
    partial void OnHoursChanged(BatchHoursSettings? value) => RecomputeValidation();
    partial void OnPaymentModeChanged(string value) => RecomputeValidation();

    // ── The two times: a date and a time of day each ───────────────────────────

    /// <summary>The day of <see cref="StartAt"/>, for the date picker.</summary>
    public DateTime? FromDate
    {
        get => StartAt.LocalDateTime.Date;
        set
        {
            if (value is { } day) StartAt = At(day, TimeOnly.FromDateTime(StartAt.LocalDateTime));
            else OnPropertyChanged();
        }
    }

    /// <summary>The time of day of <see cref="StartAt"/>, typed. Text that is no time is put back as it was.</summary>
    public string FromTime
    {
        get => StartAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            if (BatchClock.TryParse(value, out var time)) StartAt = At(StartAt.LocalDateTime.Date, time);
            OnPropertyChanged();
        }
    }

    public DateTime? ToDate
    {
        get => EndAt.LocalDateTime.Date;
        set
        {
            if (value is { } day) EndAt = At(day, TimeOnly.FromDateTime(EndAt.LocalDateTime));
            else OnPropertyChanged();
        }
    }

    public string ToTime
    {
        get => EndAt.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            if (BatchClock.TryParse(value, out var time)) EndAt = At(EndAt.LocalDateTime.Date, time);
            OnPropertyChanged();
        }
    }

    /// <summary>A local day and time of day, with the offset in force on that day.</summary>
    private static DateTimeOffset At(DateTime day, TimeOnly time)
        => new(DateTime.SpecifyKind(day.Date + time.ToTimeSpan(), DateTimeKind.Local));

    private bool CanStart() => !IsRunning && Result is null && !HasValidation && AnyKaratSelected();

    // Counts the openings, so the answer to an earlier one is dropped.
    private int _windowRequest;

    /// <summary>
    /// Asks the server when a batch may be dated from and sets the two times to run from then to now: a
    /// batch is dated after every bill already stored and never in the future (owner, 20 November
    /// 2026), and a bill dated an earlier day than it was saved counts as the end of that day (owner,
    /// 21 November 2026). With no bill yet the times start at the start of today. The answer also
    /// carries the shop hours. Called each time the dialog opens, after a batch, and by "Last bill to
    /// now". A time the operator changed while the answer was on its way is kept, unless
    /// <paramref name="resetTimes"/> says to put both back; when the time cannot be read, From and the
    /// earliest time stay as they were. Returns whether it was read.
    /// </summary>
    public async Task<bool> PrepareWindowAsync(bool resetTimes = false, CancellationToken cancellationToken = default)
    {
        var request = ++_windowRequest;
        var (startBefore, endBefore) = (StartAt, EndAt);
        SyntheticBatchWindowResponse? window = null;
        // The two times are kept from the keyboard while this is on its way: a time being typed is
        // held by its box until the cursor leaves it, and the answer would be written over it.
        IsReadingTimes = true;
        try
        {
            window = await _batchApi.GetSyntheticBatchWindowAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The server checks the times again when the batch is sent; the form just cannot say so first.
        }

        if (request != _windowRequest) return window is not null;
        IsReadingTimes = false;

        // To the minute the box shows: a second or two of clock difference with the server must
        // not make the To time it was given "later than now" there.
        var now = Now;
        if (resetTimes || EndAt == endBefore) EndAt = FloorToMinute(now);
        IsLastBillUnknown = window is null;
        if (window is null) return false;

        Hours = window.Hours;
        EarliestAt = window.EarliestUtc?.ToOffset(now.Offset);
        if (resetTimes || StartAt == startBefore) StartAt = EarliestAt ?? new DateTimeOffset(now.Date, now.Offset);
        return true;
    }

    /// <summary>True while the earliest time is being read from the server: the date and time boxes take no keys meanwhile.</summary>
    [ObservableProperty] private bool isReadingTimes;

    /// <summary>True when the last read of the earliest time failed; the line under the times says so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowNote), nameof(NextBatchNote))]
    private bool isLastBillUnknown;

    private bool CanChangeForm() => !IsRunning;

    /// <summary>Puts both times back: from just after the last bill to now.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeForm))]
    private Task LastBillToNowAsync() => PrepareWindowAsync(resetTimes: true);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAllKarats() => SetAllKarats(true);

    [RelayCommand]
    private void ClearAllKarats() => SetAllKarats(false);

    private bool AnyKaratSelected()
        => KaratOptions.Count > 0 && KaratOptions.Any(k => k.IsSelected);

    private void SetAllKarats(bool selected)
    {
        foreach (var k in KaratOptions) k.IsSelected = selected;
        StartCommand.NotifyCanExecuteChanged();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Catalog))
            RefreshKaratOptions();
    }

    /// <summary>The saved karats that name a Tally stock item, keeping the ticks of those still there.</summary>
    public void RefreshKaratOptions()
    {
        var rows = _settings?.Catalog.Karats;
        if (rows is null) return;
        var previouslySelected = KaratOptions
            .Where(k => k.IsSelected)
            .Select(k => k.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hadPrevious = KaratOptions.Count > 0;
        KaratOptions.Clear();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Label)) continue;
            if (string.IsNullOrWhiteSpace(row.TallyItem)) continue;
            var entry = new SelectableKarat
            {
                Label = row.Label.Trim(),
                TallyItem = row.TallyItem.Trim(),
                IsSelected = !hadPrevious || previouslySelected.Contains(row.Label.Trim())
            };
            entry.PropertyChanged += OnKaratSelectedChanged;
            KaratOptions.Add(entry);
        }
        OnPropertyChanged(nameof(HasKaratOptions));
        RecomputeValidation();
    }

    private void OnKaratSelectedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectableKarat.IsSelected))
            RecomputeValidation();
    }

    /// <summary>The one thing that stops Start, said in the banner across the form; null when nothing does.</summary>
    private void RecomputeValidation()
    {
        string? msg = null;
        var minBill = UiCulture.Rupees(SyntheticBatchPlanLimits.MinBillTotal);
        if (Rate24Kt <= 0m)
            msg = "Type today's 24K rate to start.";
        else if (TotalAmount < SyntheticBatchPlanLimits.MinBillTotal)
            msg = $"The total must be at least ₹{minBill}, the smallest bill a batch makes.";
        else if (MaxBillAmount < SyntheticBatchPlanLimits.MinBillTotal || MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
            msg = $"The largest single bill must be between ₹{minBill} and ₹{UiCulture.Rupees(SyntheticBatchPlanLimits.HardMaxBillAmount)}.";
        else if (MinItemsPerBill <= 0 || MaxItemsPerBill < MinItemsPerBill || MaxItemsPerBill > SyntheticBatchPlanLimits.MaxItemsPerBillCap)
            msg = $"Items per bill must be from 1 to {SyntheticBatchPlanLimits.MaxItemsPerBillCap}, the smaller number first.";
        else if (EarliestAt is { } first && first > Now)
            msg = $"The last bill leaves no time to date a batch in: a batch can be dated from {Show(first)}, which is later than now.";
        else if (EndAt > Now)
            msg = "The To time cannot be later than now.";
        else if (StartAt >= EndAt)
            msg = "The From time must be before the To time.";
        else if (EarliestAt is { } allowed && StartAt < allowed)
            msg = $"The From time cannot be before {Show(allowed)}, just after the last bill.";
        else if (!AnyKaratSelected())
            msg = "Tick at least one karat.";
        else
        {
            var (_, mostBills) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
            var open = OpenMinutes;
            if (open == 0 && Hours is { Opens: not null } or { ClosedOn: not null })
                msg = "These times have no minute inside shop hours. Pick other times, or change the hours in Settings, Admin.";
            else if (open < mostBills)
                msg = $"These times have room for {open} bill(s), and this batch can make up to {mostBills}. Lower the total, or pick a longer time.";
        }

        ValidationMessage = msg;
        StartCommand.NotifyCanExecuteChanged();
        RaiseSummaryChanged();
    }

    /// <summary>The minutes a bill can be dated at, as the server counts them: after the last bill, inside shop hours.</summary>
    private int OpenMinutes => BatchOpenMinutes.Count(StartAt, EndAt, EarliestAt, Hours);

    private static DateTimeOffset FloorToMinute(DateTimeOffset v)
        => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, v.Offset);

    private static string Show(DateTimeOffset local) => local.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// "Make bills…": asks the operator to check the figures, then sends the batch. The server makes
    /// it in one request; whatever that request comes to, the form gives way to the result.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken _)
    {
        // What the operator is asked about is what is sent and what "What was asked" shows: all
        // three are taken from the form now. The form can change while the question is open (the
        // read of the last bill's time may still be on its way), and a batch must not be made for
        // other times than the ones that were approved.
        var asked = BuildAskedRows();
        var approved = BuildRequest();
        if (!await _dialogs.ConfirmAsync(BuildConfirmation())) return;

        _cts = new CancellationTokenSource();
        Asked = asked;
        IsRunning = true;
        RunState = "Running";
        StatusMessage = "Checking that admin is unlocked…";
        // The server keeps how far this batch got under this id, for when the request has no answer.
        var batchId = Guid.NewGuid();
        var sent = false;

        try
        {
            var token = await EnsureAdminTokenAsync(_cts.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusMessage = string.Empty;
                RunState = "Not started";
                return;
            }

            StatusMessage = "Making the bills. This can take up to a minute.";
            var request = approved with { BatchId = batchId };
            sent = true;
            var response = await _batchApi.CreateSyntheticBatchAsync(request, token, _cts.Token);

            await ShowMadeAsync(response.BillCount, response.TotalAmount, response.FirstInvoiceNumber, response.LastInvoiceNumber);
        }
        catch (Exception ex) when (sent)
        {
            await ShowStoppedAsync(batchId, ex, cancelledByOperator: _cts.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            RunState = "Failed";
            Result = new BatchResult("err", "The batch was not made", ex.Message, "Nothing was saved.", BillsMayBeSaved: false);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>The batch was made whole: says so, loads the Bills tab again and moves the times past the new bills.</summary>
    private async Task ShowMadeAsync(int billCount, decimal total, string? firstNumber, string? lastNumber)
    {
        RunState = "Done";
        await ShowResultAsync(new BatchResult(
            "ok",
            $"{Bills(billCount)} made",
            string.Empty,
            $"They come to **₹{UiCulture.Rupees(total)}** and are in the Bills tab as **pending**. Nothing was sent to Tally.",
            BillsMayBeSaved: true,
            firstNumber,
            lastNumber));
    }

    /// <summary>
    /// Shows a result in place of the form. When bills are, or may be, stored that were not before,
    /// the Bills tab is loaded again and the times are put after the bills now stored: the next
    /// batch goes after them.
    /// </summary>
    private async Task ShowResultAsync(BatchResult result, bool billsChanged = true)
    {
        Result = result;
        if (!billsChanged) return;
        BillsSaved?.Invoke(this, EventArgs.Empty);
        await PrepareWindowAsync(resetTimes: true);
    }

    /// <summary>
    /// The request ended without the batch's answer: the operator cancelled it, the server took too
    /// long, the connection dropped, or the server refused or failed part-way. A batch keeps the
    /// bills it saved before it stopped, so the server is asked how many that was; the dialog says
    /// so, and the Bills tab and the times follow the bills now stored. Before 21 November 2026 a
    /// stopped batch said only "cancelled" or "failed" and left both as they were.
    /// </summary>
    private async Task ShowStoppedAsync(Guid batchId, Exception failure, bool cancelledByOperator)
    {
        // An answer from the server, even a refusal, means it has stopped: nothing is still being saved.
        var answered = failure is ApiException;
        var why = cancelledByOperator ? "You cancelled the run."
            : failure is OperationCanceledException ? "The server took too long to answer."
            : failure.Message.TrimEnd().TrimEnd('.') + ".";

        StatusMessage = "Asking the server how many bills were saved…";
        var (outcome, serverAnswered) = await ReadOutcomeAsync(batchId, waitForRecord: !answered);

        // A refusal the server makes before a batch is planned (a time outside the window, no item
        // to bill, admin locked again) leaves nothing under the batch's id: no bill was saved, so
        // the Bills tab and the times stay as they are. The same statuses can also end a batch
        // that has saved bills (a bill that fails its checks part-way is a 400), and then there
        // is a record, which is what is believed. "Nothing under the id" has to be the server's
        // own answer: when it could not be asked, how many bills were saved is not known.
        if (outcome is null
            && serverAnswered
            && failure is ApiException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
        {
            RunState = "Failed";
            Result = new BatchResult("err", "The batch was not made", why, "Nothing was saved.", BillsMayBeSaved: false);
            // The refusal may be that the shop hours were changed: the form shows the saved ones again.
            _ = HoursEditor.LoadAsync();
            return;
        }

        if (outcome is { Finished: true, PlannedBillCount: > 0 } whole && whole.BillCount == whole.PlannedBillCount)
        {
            // Every bill was saved and only the answer was lost, so what the request said went wrong did not.
            await ShowMadeAsync(whole.BillCount, whole.TotalAmount, whole.FirstInvoiceNumber, whole.LastInvoiceNumber);
            return;
        }

        RunState = answered ? "Failed" : "Stopped";
        const string lookFirst = "Look in the Bills tab before you start another batch.";
        switch (outcome)
        {
            case { Finished: true, LastSaveInDoubt: true }:
                // The batch stopped at a save that did not say whether its bill was stored.
                await ShowResultAsync(new BatchResult(
                    "warn",
                    outcome.BillCount == 0
                        ? "The batch stopped at its first bill"
                        : $"Stopped after {outcome.BillCount} of {Bills(outcome.PlannedBillCount)}",
                    why,
                    "One more bill may have been saved than the server counted. " + lookFirst,
                    BillsMayBeSaved: true,
                    outcome.FirstInvoiceNumber,
                    outcome.LastInvoiceNumber));
                break;

            case { Finished: true, BillCount: 0 } when !answered:
                // Stopped by this Desktop before the first bill: nothing is stored that was not before.
                await ShowResultAsync(
                    new BatchResult("warn", "Stopped before any bill was saved", why,
                        "Nothing was saved, so you can change the form and start again.", BillsMayBeSaved: false),
                    billsChanged: false);
                break;

            case { Finished: true, BillCount: 0 }:
                // The server stopped the batch before its first bill: a bill saved at a counter is
                // what stopped it, so the times have to move past that bill.
                await ShowResultAsync(new BatchResult("warn", "Stopped before any bill was saved", why,
                    "This batch saved nothing. The times now start after the last bill.", BillsMayBeSaved: false));
                break;

            case null:
                await ShowResultAsync(new BatchResult("warn", "How many bills were saved is not known",
                    $"The batch did not finish. {why}", lookFirst, BillsMayBeSaved: true));
                break;

            case { Finished: false }:
                await ShowResultAsync(new BatchResult("warn", "The server is still making the batch",
                    $"{outcome.BillCount} of {Bills(outcome.PlannedBillCount)} are saved so far. {why}", lookFirst, BillsMayBeSaved: true));
                break;

            default:
                await ShowResultAsync(new BatchResult(
                    "warn",
                    $"Stopped after {outcome.BillCount} of {Bills(outcome.PlannedBillCount)}",
                    why,
                    $"The {Bills(outcome.BillCount, "saved ")} {(outcome.BillCount == 1 ? "is" : "are")} in the Bills tab as **pending**. Nothing was sent to Tally.",
                    BillsMayBeSaved: true,
                    outcome.FirstInvoiceNumber,
                    outcome.LastInvoiceNumber));
                break;
        }
    }

    /// <summary>"1 bill", "12 bills"; with a word before the noun, "3 saved bills".</summary>
    private static string Bills(int count, string before = "") => $"{count} {before}{(count == 1 ? "bill" : "bills")}";

    /// <summary>How often the server is asked how far a stopped batch got: quickly at first, then slowly.</summary>
    private static readonly TimeSpan OutcomePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SlowOutcomePollInterval = TimeSpan.FromSeconds(2);
    private const int QuickOutcomePolls = 6;
    private const int OutcomePollsWithNoRecord = 3;

    /// <summary>
    /// How far the batch got, as the server holds it; null when it holds nothing or cannot be
    /// reached. A cancelled request stops the server after the bill in hand, so it is asked until
    /// it says the batch has finished. That is waited for as long as a batch may take, with the
    /// dialog still running: a save on a stalled line can outlast any short wait, and Start must
    /// not come back while the server is saving, or the count and the next start time would both
    /// be stale. When the request had no answer the record may not be there yet, so that too is
    /// waited for, briefly.
    /// </summary>
    private async Task<(SyntheticBatchOutcomeResponse? Outcome, bool ServerAnswered)> ReadOutcomeAsync(Guid batchId, bool waitForRecord)
    {
        SyntheticBatchOutcomeResponse? outcome = null;
        var missing = 0;
        var began = _time.GetTimestamp();
        for (var attempt = 0; _time.GetElapsedTime(began) < SyntheticBatchApiClient.BatchTimeout; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(attempt <= QuickOutcomePolls ? OutcomePollInterval : SlowOutcomePollInterval, _time);
            try
            {
                outcome = await _batchApi.GetSyntheticBatchOutcomeAsync(batchId);
            }
            catch (Exception)
            {
                // The server cannot be reached, so what it holds is not known.
                return (null, false);
            }

            if (outcome is { Finished: true }) return (outcome, true);
            if (outcome is null && (!waitForRecord || ++missing >= OutcomePollsWithNoRecord)) return (null, true);
            if (outcome is not null)
                StatusMessage = $"The server is finishing the bill in hand: {outcome.BillCount} of {Bills(outcome.PlannedBillCount)} saved so far…";
        }

        return (outcome, true);
    }

    private SyntheticBatchRequest BuildRequest()
    {
        var selected = KaratOptions
            .Where(k => k.IsSelected)
            .Select(k => k.Label)
            .ToList();
        return new SyntheticBatchRequest(
            TotalAmount: TotalAmount,
            MaxBillAmount: MaxBillAmount,
            Rate24Kt: Rate24Kt,
            PaymentMode: PaymentMode,
            MinItemsPerBill: MinItemsPerBill,
            MaxItemsPerBill: MaxItemsPerBill,
            StartAtUtc: StartAt.ToUniversalTime(),
            EndAtUtc: EndAt.ToUniversalTime(),
            SelectedKaratLabels: selected,
            // The hours shown beside the form and in the question; the server refuses the batch
            // when they have been changed since, instead of dating bills by hours nobody saw.
            ExpectedHours: Hours);
    }

    private async Task<string?> EnsureAdminTokenAsync(CancellationToken cancellationToken)
    {
        var current = _adminTokens?.Current?.Token;
        if (!string.IsNullOrWhiteSpace(current)) return current;
        try
        {
            await _dialogs.RequestAdminUnlockAsync(cancellationToken);
        }
        catch (OperationCanceledException) { return null; }
        finally
        {
            // The unlock dialog took this dialog's place. It comes back, so the run and what it
            // comes to are in front of the operator who started it.
            _dialogs.Show(ShellDialogs.SyntheticBatch);
        }

        return _adminTokens?.Current?.Token;
    }
}

public partial class SelectableKarat : ObservableObject
{
    [ObservableProperty] private string label = string.Empty;
    [ObservableProperty] private string tallyItem = string.Empty;
    [ObservableProperty] private bool isSelected = true;
}
