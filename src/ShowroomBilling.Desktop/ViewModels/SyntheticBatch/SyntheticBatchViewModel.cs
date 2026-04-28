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

    private readonly IBillsApiClient? _billsApi;
    private readonly AdminTokenStore? _adminTokens;
    private readonly SettingsViewModel? _settings;

    /// <summary>
    /// Delegate wired by <c>MainWindowViewModel</c> to open the admin-unlock dialog
    /// when the batch is started without an active admin session.
    /// </summary>
    public Func<CancellationToken, Task>? AdminUnlockHandler { get; set; }

    /// <summary>Fires on successful completion so the Bills tab can refresh.</summary>
    public event EventHandler? BatchCompleted;

    public SyntheticBatchViewModel() : this(null, null, null) { }

    public SyntheticBatchViewModel(
        IBillsApiClient? billsApi,
        AdminTokenStore? adminTokens,
        SettingsViewModel? settings)
    {
        _billsApi = billsApi;
        _adminTokens = adminTokens;
        _settings = settings;

        KaratOptions = new ObservableCollection<SelectableKarat>();
        PaymentOptions = new ObservableCollection<string> { "Cash", "Credit and debit" };

        // Commands must be initialised BEFORE any [ObservableProperty] setter that
        // triggers RecomputeValidation, because the validation path calls
        // StartCommand.NotifyCanExecuteChanged().
        StartCommand = new AsyncRelayCommand(RunBatchAsync, CanStart);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        SelectAllKaratsCommand = new RelayCommand(() => SetAllKarats(true));
        ClearAllKaratsCommand = new RelayCommand(() => SetAllKarats(false));

        var now = DateTimeOffset.Now;
        var localStart = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 0, 0, now.Offset);
        StartAt = localStart > now ? localStart : new DateTimeOffset(now.DateTime.Date, now.Offset);
        EndAt = new DateTimeOffset(now.DateTime.Date.AddDays(0).AddHours(23).AddMinutes(59), now.Offset);

        if (_settings is not null)
        {
            _settings.PropertyChanged += OnSettingsPropertyChanged;
            if (_settings.Draft is not null)
                _settings.Draft.PropertyChanged += OnSettingsDraftPropertyChanged;
            RefreshKaratOptions();
        }

        RecomputeValidation();
    }

    private CancellationTokenSource? _cts;

    public ObservableCollection<SelectableKarat> KaratOptions { get; }
    public ObservableCollection<string> PaymentOptions { get; }

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand SelectAllKaratsCommand { get; }
    public IRelayCommand ClearAllKaratsCommand { get; }

    [ObservableProperty] private long totalAmount = 1_000_000;
    [ObservableProperty] private long maxBillAmount = SyntheticBatchPlanLimits.HardMaxBillAmount;
    [ObservableProperty] private decimal rate24Kt = 7200m;
    [ObservableProperty] private string paymentMode = "Cash";
    [ObservableProperty] private int minItemsPerBill = 1;
    [ObservableProperty] private int maxItemsPerBill = 3;
    [ObservableProperty] private DateTimeOffset startAt;
    [ObservableProperty] private DateTimeOffset endAt;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? validationMessage;
    [ObservableProperty] private int createdCount;
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private decimal grandTotalSoFar;

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
    partial void OnPaymentModeChanged(string value) => RecomputeValidation();
    partial void OnValidationMessageChanged(string? value) => OnPropertyChanged(nameof(HasValidation));
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanStart() => !IsRunning && !HasValidation && AnyKaratSelected();

    private bool AnyKaratSelected()
        => KaratOptions.Count > 0 && KaratOptions.Any(k => k.IsSelected);

    private void SetAllKarats(bool selected)
    {
        foreach (var k in KaratOptions) k.IsSelected = selected;
        StartCommand.NotifyCanExecuteChanged();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Settings) or nameof(SettingsViewModel.Draft))
            RefreshKaratOptions();
    }

    private void OnSettingsDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName?.Contains("Karat") == true)
            RefreshKaratOptions();
    }

    public void RefreshKaratOptions()
    {
        var rows = _settings?.Draft?.KaratRows;
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
        if (TotalAmount <= 0)
            msg = "Total target amount must be greater than zero.";
        else if (MaxBillAmount <= 0 || MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
            msg = $"Max bill amount must be between ₹1 and ₹{SyntheticBatchPlanLimits.HardMaxBillAmount.ToString("N0", AmountCulture)}.";
        else if (Rate24Kt <= 0m)
            msg = "24kt rate must be greater than zero.";
        else if (MinItemsPerBill <= 0 || MaxItemsPerBill < MinItemsPerBill)
            msg = "Items per bill range is invalid.";
        else if (StartAt >= EndAt)
            msg = "Start time must be before end time.";
        else if (!AnyKaratSelected())
            msg = "Select at least one Tally-mapped karat.";
        else
        {
            var (minBills, maxBills) = SyntheticBillPlanner.EstimateBillCountBounds(TotalAmount, MaxBillAmount);
            var slots = AvailableMinuteSlots(StartAt, EndAt);
            if (slots < maxBills)
                msg = $"Window fits {slots} minute slot(s) but up to {maxBills} bills may be generated. Widen window or lower total.";
            else if (slots < minBills)
                msg = $"Window fits only {slots} slot(s); at least {minBills} required.";
        }
        ValidationMessage = msg;
        StartCommand.NotifyCanExecuteChanged();
    }

    private static int AvailableMinuteSlots(DateTimeOffset start, DateTimeOffset end)
    {
        if (start >= end) return 0;
        var s = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, start.Offset);
        if (s != start) s = s.AddMinutes(1);
        var e = new DateTimeOffset(end.Year, end.Month, end.Day, end.Hour, end.Minute, 0, end.Offset);
        if (e < s) return 0;
        return (int)(e - s).TotalMinutes + 1;
    }

    private async Task RunBatchAsync(CancellationToken _)
    {
        if (_billsApi is null)
        {
            ErrorMessage = "Bills API is not wired.";
            return;
        }

        _cts = new CancellationTokenSource();
        ErrorMessage = null;
        CreatedCount = 0;
        TotalCount = 0;
        GrandTotalSoFar = 0m;
        IsRunning = true;
        StatusMessage = "Resolving admin session…";

        try
        {
            var token = await EnsureAdminTokenAsync(_cts.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                StatusMessage = "Admin unlock cancelled.";
                return;
            }

            StatusMessage = "Submitting synthetic batch to API…";
            var request = BuildRequest();
            var response = await _billsApi.CreateSyntheticBatchAsync(request, token, _cts.Token);

            CreatedCount = response.BillCount;
            TotalCount = response.BillCount;
            GrandTotalSoFar = response.TotalAmount;
            StatusMessage = $"Created {response.BillCount} bill(s) · ₹{response.TotalAmount:N0}.";
            BatchCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Batch cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Batch failed.";
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
        if (AdminUnlockHandler is null) return null;
        try
        {
            await AdminUnlockHandler(cancellationToken);
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
