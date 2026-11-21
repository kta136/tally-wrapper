using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

public partial class SyntheticBatchViewModel : ObservableObject
{
    private static readonly CultureInfo AmountCulture = CultureInfo.GetCultureInfo("en-IN");

    private readonly IBillsApiClient _billsApi;
    private readonly IDialogService _dialogs;
    private readonly AdminTokenStore? _adminTokens;
    private readonly SettingsViewModel? _settings;
    private readonly TimeProvider _time;

    /// <summary>Fires on successful completion so the Bills tab can refresh.</summary>
    public event EventHandler? BatchCompleted;

    public SyntheticBatchViewModel(
        IBillsApiClient billsApi,
        IDialogService dialogs,
        AdminTokenStore? adminTokens,
        SettingsViewModel? settings,
        TimeProvider? time = null)
    {
        _billsApi = billsApi;
        _dialogs = dialogs;
        _adminTokens = adminTokens;
        _settings = settings;
        _time = time ?? TimeProvider.System;

        KaratOptions = new ObservableCollection<SelectableKarat>();
        PaymentOptions = new ObservableCollection<string> { Contracts.Bills.PaymentMode.Cash, Contracts.Bills.PaymentMode.CreditDebit };

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
    [ObservableProperty] private DateTimeOffset startAt;
    [ObservableProperty] private DateTimeOffset endAt;

    /// <summary>
    /// The first minute a batch may date a bill, in local time, as the server works it out: just after
    /// the last bill, where a bill dated an earlier day than it was saved counts as the end of that day.
    /// Null when there is no bill or it could not be read.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowNote))]
    private DateTimeOffset? earliestAt;

    private DateTimeOffset Now => _time.GetLocalNow();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool isRunning;

    [ObservableProperty] private string statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidation))]
    private string? validationMessage;

    public bool HasValidation => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasKaratOptions => KaratOptions.Count > 0;

    partial void OnTotalAmountChanged(long value) => RecomputeValidation();
    partial void OnMaxBillAmountChanged(long value) => RecomputeValidation();
    partial void OnRate24KtChanged(decimal value) => RecomputeValidation();
    partial void OnMinItemsPerBillChanged(int value) => RecomputeValidation();
    partial void OnMaxItemsPerBillChanged(int value) => RecomputeValidation();
    partial void OnStartAtChanged(DateTimeOffset value) => RecomputeValidation();
    partial void OnEndAtChanged(DateTimeOffset value) => RecomputeValidation();
    partial void OnEarliestAtChanged(DateTimeOffset? value) => RecomputeValidation();
    partial void OnPaymentModeChanged(string value) => RecomputeValidation();

    private bool CanStart() => !IsRunning && !HasValidation && AnyKaratSelected();

    private const string LastBillUnknown = "Could not read when the last bill was made; the server checks the times when the batch starts.";

    // Counts the openings, so the answer to an earlier one is dropped.
    private int _windowRequest;

    /// <summary>
    /// Asks the server when a batch may be dated from and sets the window to run from then to now: a
    /// batch is dated after every bill already stored and never in the future (owner, 20 November
    /// 2026), and a bill dated an earlier day than it was saved counts as the end of that day (owner,
    /// 21 November 2026). With no bill yet the window starts at the start of today. Called each time
    /// the dialog opens and after a batch. A time the operator changed while the answer was on its way
    /// is kept; when the time cannot be read, Start and the earliest time stay as they were. Returns
    /// whether it was read.
    /// </summary>
    public async Task<bool> PrepareWindowAsync(CancellationToken cancellationToken = default)
    {
        var request = ++_windowRequest;
        var (startBefore, endBefore) = (StartAt, EndAt);
        DateTimeOffset? earliest = null;
        var known = true;
        try
        {
            earliest = (await _billsApi.GetSyntheticBatchWindowAsync(cancellationToken)).EarliestUtc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The server checks the window again when the batch is sent; the form just cannot say so first.
            known = false;
        }

        if (request != _windowRequest) return known;

        // To the minute the box shows: a second or two of clock difference with the server must
        // not make the End it was given "later than now" there.
        var now = Now;
        if (EndAt == endBefore) EndAt = FloorToMinute(now);
        if (!known)
        {
            StatusMessage = LastBillUnknown;
            return false;
        }

        EarliestAt = earliest?.ToOffset(now.Offset);
        if (StartAt == startBefore) StartAt = EarliestAt ?? new DateTimeOffset(now.Date, now.Offset);
        return true;
    }

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

    private void RecomputeValidation()
    {
        string? msg = null;
        var minBill = SyntheticBatchPlanLimits.MinBillTotal.ToString("N0", AmountCulture);
        if (TotalAmount < SyntheticBatchPlanLimits.MinBillTotal)
            msg = $"Total target amount must be at least ₹{minBill}, the smallest bill a batch makes.";
        else if (MaxBillAmount < SyntheticBatchPlanLimits.MinBillTotal || MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
            msg = $"Max bill amount must be between ₹{minBill} and ₹{SyntheticBatchPlanLimits.HardMaxBillAmount.ToString("N0", AmountCulture)}.";
        else if (Rate24Kt <= 0m)
            msg = "24kt rate must be greater than zero.";
        else if (MinItemsPerBill <= 0 || MaxItemsPerBill < MinItemsPerBill)
            msg = "Items per bill range is invalid.";
        else if (EarliestAt is { } first && first > Now)
            msg = $"The last bill leaves no time to date a batch in: a batch can be dated from {Show(first)}, which is later than now.";
        else if (EndAt > Now)
            msg = "End time cannot be later than now.";
        else if (StartAt >= EndAt)
            msg = "Start time must be before end time.";
        else if (EarliestAt is { } allowed && StartAt < allowed)
            msg = $"Start time cannot be before {Show(allowed)}, the first time after the last bill.";
        else if (!AnyKaratSelected())
            msg = "Select at least one Tally-mapped karat.";
        else
        {
            var (minBills, maxBills) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
            var slots = AvailableMinuteSlots(StartAt, EndAt, EarliestAt);
            if (slots < maxBills)
                msg = $"Window fits {slots} minute slot(s) but up to {maxBills} bills may be generated. Lower the total, or widen the window (it can run from the last bill to now).";
            else if (slots < minBills)
                msg = $"Window fits only {slots} slot(s); at least {minBills} required.";
        }
        ValidationMessage = msg;
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The minutes a bill can be given, as the server counts them: none before the earliest.</summary>
    private static int AvailableMinuteSlots(DateTimeOffset start, DateTimeOffset end, DateTimeOffset? earliest)
    {
        if (start >= end) return 0;
        var s = FloorToMinute(start);
        if (s != start) s = s.AddMinutes(1);
        if (earliest is { } first && first > s) s = first;
        var e = FloorToMinute(end);
        if (e < s) return 0;
        return (int)(e - s).TotalMinutes + 1;
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset v)
        => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, v.Offset);

    private static string Show(DateTimeOffset local) => local.ToString("dd MMM, HH:mm", CultureInfo.InvariantCulture);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken _)
    {
        _cts = new CancellationTokenSource();
        ErrorMessage = null;
        IsRunning = true;
        RunState = "Running";
        StatusMessage = "Resolving admin session…";

        try
        {
            var token = await EnsureAdminTokenAsync(_cts.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusMessage = "Admin unlock cancelled.";
                RunState = "Not started";
                return;
            }

            StatusMessage = "Submitting synthetic batch to API…";
            var request = BuildRequest();
            var response = await _billsApi.CreateSyntheticBatchAsync(request, token, _cts.Token);

            StatusMessage = $"Created {response.BillCount} bill(s) · ₹{response.TotalAmount:N0}.";
            RunState = "Done";
            BatchCompleted?.Invoke(this, EventArgs.Empty);
            // The next batch goes after these bills.
            var result = StatusMessage;
            var refreshed = await PrepareWindowAsync();
            StatusMessage = refreshed ? result : $"{result} {LastBillUnknown}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Batch cancelled.";
            RunState = "Stopped";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Batch failed.";
            RunState = "Failed";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
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
            SelectedKaratLabels: selected);
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
        return _adminTokens?.Current?.Token;
    }
}

public partial class SelectableKarat : ObservableObject
{
    [ObservableProperty] private string label = string.Empty;
    [ObservableProperty] private string tallyItem = string.Empty;
    [ObservableProperty] private bool isSelected = true;
}
