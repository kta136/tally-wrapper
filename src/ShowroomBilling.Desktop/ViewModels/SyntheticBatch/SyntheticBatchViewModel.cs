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
        EndAt = now;

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

    /// <summary>When the newest stored bill was made, in local time; null when there is none or it could not be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowNote))]
    private DateTimeOffset? lastBillAt;

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
    partial void OnLastBillAtChanged(DateTimeOffset? value) => RecomputeValidation();
    partial void OnPaymentModeChanged(string value) => RecomputeValidation();

    private bool CanStart() => !IsRunning && !HasValidation && AnyKaratSelected();

    /// <summary>
    /// Reads when the newest bill was made and sets the window to run from it to now: a batch is dated
    /// after every bill already stored and never in the future (owner, 20 November 2026). With no bill
    /// yet the window starts at the start of today. Called each time the dialog opens and after a batch.
    /// </summary>
    public async Task PrepareWindowAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset? newest = null;
        var known = true;
        try
        {
            var latest = await _billsApi.SearchAsync(
                new BillSearchFilter(State: null, FromDate: null, ToDate: null, Skip: 0, Take: 1,
                    Sort: BillSearchSorts.CreatedDesc, IncludeTotal: false),
                cancellationToken);
            newest = latest.Items.FirstOrDefault()?.CreatedAtUtc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The server checks the window again when the batch is sent; the form just cannot say so first.
            known = false;
        }

        var now = Now;
        LastBillAt = newest?.ToOffset(now.Offset);
        StartAt = LastBillAt ?? new DateTimeOffset(now.Date, now.Offset);
        EndAt = now;
        if (!known)
            StatusMessage = "Could not read when the last bill was made; the server checks the times when the batch starts.";
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
        else if (LastBillAt is { } newest && newest > Now)
            msg = $"The last bill is dated {Show(newest)}, later than now, so there is no time left to date a batch in.";
        else if (EndAt > Now)
            msg = "End time cannot be later than now.";
        else if (StartAt >= EndAt)
            msg = "Start time must be before end time.";
        else if (LastBillAt is { } last && StartAt < FloorToMinute(last))
            msg = $"Start time cannot be before the last bill, {Show(last)}.";
        else if (!AnyKaratSelected())
            msg = "Select at least one Tally-mapped karat.";
        else
        {
            var (minBills, maxBills) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
            var slots = AvailableMinuteSlots(StartAt, EndAt, LastBillAt);
            if (slots < maxBills)
                msg = $"Window fits {slots} minute slot(s) but up to {maxBills} bills may be generated. Lower the total, or widen the window (it can run from the last bill to now).";
            else if (slots < minBills)
                msg = $"Window fits only {slots} slot(s); at least {minBills} required.";
        }
        ValidationMessage = msg;
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The minutes a bill can be given, as the server counts them: none in or before the last bill's minute.</summary>
    private static int AvailableMinuteSlots(DateTimeOffset start, DateTimeOffset end, DateTimeOffset? lastBill)
    {
        if (start >= end) return 0;
        var s = FloorToMinute(start);
        if (s != start) s = s.AddMinutes(1);
        if (lastBill is { } last && FloorToMinute(last).AddMinutes(1) > s) s = FloorToMinute(last).AddMinutes(1);
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
            await PrepareWindowAsync();
            StatusMessage = result;
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
