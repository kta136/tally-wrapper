using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

public partial class InvoiceViewModel : ObservableObject
{
    private readonly IBillsApiClient? _billsApi;
    private readonly INumberingApiClient? _numberingApi;
    private readonly SettingsViewModel? _settings;
    private readonly IApiReadinessSignal? _apiReadiness;
    private readonly InvoiceQuickAddWorkflow _quickAdd;
    private readonly InvoiceLineCollectionObserver _lineCollectionObserver;
    private readonly HashSet<ItemMasterRowVm> _observedItemMasters = new();
    private Dictionary<string, ItemMasterRowVm>? _itemMasterByName;
    private bool _deferLineCollectionRecompute;
    private bool _syncingLinkedTotals;
    private Guid? _draftBillId;
    private const string InvoiceNumberPlaceholder = "SR/25-26/0000";
    private const decimal CgstRate = BillCalculator.CgstRate;
    private const decimal SgstRate = BillCalculator.SgstRate;
    /// <summary>
    /// Upper bound on the readiness wait when <c>RefreshNextNumberAsync(waitForApi: true)</c>
    /// is called. If the API is genuinely down past this we bail to the
    /// placeholder and let the regular "Save" path surface the failure —
    /// hanging the preview forever would only delay the operator's feedback.
    /// </summary>
    private static readonly TimeSpan ApiReadinessWait = TimeSpan.FromSeconds(10);

    public InvoiceViewModel() : this(null, null, null, null) { }

    public InvoiceViewModel(
        IBillsApiClient? billsApi,
        INumberingApiClient? numberingApi,
        SettingsViewModel? settings,
        IApiReadinessSignal? apiReadiness = null)
    {
        _billsApi = billsApi;
        _numberingApi = numberingApi;
        _settings = settings;
        _apiReadiness = apiReadiness;

        Lines = new ObservableCollection<BillLineViewModel>();
        AddTrailingBlankRow();

        AddRowCommand = new RelayCommand(() => AddTrailingBlankRow(deferCollectionRecompute: true));
        RemoveFocusedRowCommand = new RelayCommand(RemoveFocusedRow, () => Lines.Count > 1);
        RemoveLineCommand = new RelayCommand<BillLineViewModel?>(RemoveLine, line => line is not null && Lines.Count > 1);
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync, CanSaveDraft);
        ClearCommand = new RelayCommand(ClearInvoice);
        PrintPreviewCommand = new RelayCommand(PrintPreview);
        _lineCollectionObserver = new InvoiceLineCollectionObserver(
            Lines,
            OnLineMutated,
            OnLineCollectionChanged);
        QuickAddResults = new ObservableCollection<ItemMasterRowVm>();
        _quickAdd = new InvoiceQuickAddWorkflow(
            () => ItemMasters,
            Lines,
            QuickAddResults,
            CreateBlankRow,
            () => QuickAddQuery,
            value => QuickAddQuery = value,
            () => QuickAddSelection,
            value => QuickAddSelection = value);
        QuickAddCommitCommand = new RelayCommand<ItemMasterRowVm?>(CommitQuickAdd);
        AttachItemMasterObservers();
        RefreshQuickAddResults();

        Recompute();
    }

    public ObservableCollection<BillLineViewModel> Lines { get; }

    public bool HasMultipleRows => Lines.Count > 1;

    public IRelayCommand AddRowCommand { get; }
    public IRelayCommand RemoveFocusedRowCommand { get; }
    public IRelayCommand<BillLineViewModel?> RemoveLineCommand { get; }
    public IAsyncRelayCommand SaveDraftCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand PrintPreviewCommand { get; }
    public IRelayCommand<ItemMasterRowVm?> QuickAddCommitCommand { get; }
    public ObservableCollection<ItemMasterRowVm> QuickAddResults { get; }

    [ObservableProperty] private string quickAddQuery = string.Empty;
    [ObservableProperty] private ItemMasterRowVm? quickAddSelection;

    partial void OnQuickAddQueryChanged(string value) => _quickAdd.RefreshResults();

    [ObservableProperty] private string saveStatus = string.Empty;
    [ObservableProperty] private DateTimeOffset? lastSavedAtUtc;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? currentBillState;
    [ObservableProperty] private bool rateMissing;
    [ObservableProperty] private bool isEditingPosted;
    [ObservableProperty] private bool isEditingExistingBill;

    public bool IsDraftSaved => _draftBillId.HasValue;

    public event EventHandler<Guid>? SaveCompletedReadyForPrint;
    public event EventHandler<BillLineViewModel>? QuickAddCommitted;

    public ObservableCollection<ItemMasterRowVm>? ItemMasters => _settings?.Draft.ItemMasterRows;
    public ObservableCollection<KaratMasterRowVm>? KaratMasters => _settings?.Draft.KaratRows;
    public IReadOnlyList<string> UnitOptions { get; } = ItemUnits.All;
    public IReadOnlyList<string> PaymentOptions { get; } = [PaymentMode.CreditDebit, PaymentMode.Cash];

    [ObservableProperty] private string invoiceNumber = InvoiceNumberPlaceholder;
    [ObservableProperty] private DateTimeOffset billDate = DateTimeOffset.Now;
    [ObservableProperty] private string partyName = string.Empty;

    public DateTime BillDateLocal
    {
        get => BillDate.LocalDateTime;
        set
        {
            if (BillDate.LocalDateTime == value) return;
            BillDate = new DateTimeOffset(value, DateTimeOffset.Now.Offset);
        }
    }

    partial void OnBillDateChanged(DateTimeOffset value) => OnPropertyChanged(nameof(BillDateLocal));
    [ObservableProperty] private string payment = "Cash";
    [ObservableProperty] private decimal? rate24Kt;
    [ObservableProperty] private decimal discount;
    [ObservableProperty] private bool discountEnabled;
    [ObservableProperty] private string discountPercentHint = string.Empty;
    [ObservableProperty] private string narration = string.Empty;

    [ObservableProperty] private decimal subtotal;
    [ObservableProperty] private decimal cgst;
    [ObservableProperty] private decimal sgst;
    [ObservableProperty] private decimal roundOff;
    [ObservableProperty] private decimal grandTotal;
    [ObservableProperty] private decimal finalAmount;
    [ObservableProperty] private int itemCount;
    [ObservableProperty] private decimal totalWeight;

    [ObservableProperty] private int focusedRowIndex;

    partial void OnRate24KtChanged(decimal? value)
    {
        if (value is > 0m) RateMissing = false;
        Recompute();
    }
    partial void OnDiscountChanged(decimal value)
    {
        if (_syncingLinkedTotals) return;

        if (value < 0m)
        {
            SetDiscountFromLinkedTotals(0m);
            SaveStatus = "Discount cannot be negative.";
            RecomputeTotalsFromCurrentLines();
            return;
        }

        if (value > 0m && !DiscountEnabled)
        {
            SetDiscountEnabledFromLinkedTotals(true);
        }

        RecomputeTotalsFromCurrentLines();
    }

    partial void OnDiscountEnabledChanged(bool value)
    {
        if (_syncingLinkedTotals) return;
        if (!value) Discount = 0m;
        else RecomputeTotalsFromCurrentLines();
    }

    partial void OnFinalAmountChanged(decimal value)
    {
        if (_syncingLinkedTotals) return;
        ApplyFinalAmount(value);
    }

    private void OnLineCollectionChanged()
    {
        RemoveFocusedRowCommand.NotifyCanExecuteChanged();
        RemoveLineCommand.NotifyCanExecuteChanged();
        SaveDraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasMultipleRows));
        if (!_deferLineCollectionRecompute)
            Recompute();
    }

    private void OnLineMutated(object? sender, EventArgs e)
    {
        if (sender is not BillLineViewModel line)
        {
            SaveDraftCommand.NotifyCanExecuteChanged();
            Recompute();
            return;
        }

        TryAttachMasterByName(line);
        RecomputeLine(line);

        var index = Lines.IndexOf(line);
        if (index == Lines.Count - 1 && !line.IsEmpty)
        {
            AddTrailingBlankRow(deferCollectionRecompute: true);
        }

        SaveDraftCommand.NotifyCanExecuteChanged();
        RecomputeTotalsFromCurrentLines();
    }

    private void TryAttachMasterByName(BillLineViewModel line)
    {
        if (line.ItemMaster is not null) return;
        if (string.IsNullOrWhiteSpace(line.ItemName)) return;

        var typed = line.ItemName.Trim();
        if (GetItemMasterNameIndex().TryGetValue(typed, out var master))
        {
            line.ItemMaster = master;
        }
    }

    private IReadOnlyDictionary<string, ItemMasterRowVm> GetItemMasterNameIndex()
    {
        if (_itemMasterByName is not null)
        {
            return _itemMasterByName;
        }

        var index = new Dictionary<string, ItemMasterRowVm>(StringComparer.OrdinalIgnoreCase);
        if (ItemMasters is not null)
        {
            foreach (var master in ItemMasters)
            {
                var name = master.Name?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    index.TryAdd(name, master);
                }
            }
        }

        _itemMasterByName = index;
        return _itemMasterByName;
    }

    private void AttachItemMasterObservers()
    {
        if (ItemMasters is not { } masters) return;

        if (masters is INotifyCollectionChanged itemMasterCollection)
            itemMasterCollection.CollectionChanged += OnItemMastersCollectionChanged;

        foreach (var master in masters)
            AttachItemMaster(master);
    }

    private void OnItemMastersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var master in _observedItemMasters.ToArray())
                DetachItemMaster(master);
        }

        if (e.OldItems is not null)
        {
            foreach (ItemMasterRowVm master in e.OldItems)
                DetachItemMaster(master);
        }

        if (e.NewItems is not null)
        {
            foreach (ItemMasterRowVm master in e.NewItems)
                AttachItemMaster(master);
        }

        if (ItemMasters is not null)
        {
            foreach (var master in ItemMasters)
                AttachItemMaster(master);
        }

        InvalidateItemMasterIndexes();
    }

    private void AttachItemMaster(ItemMasterRowVm master)
    {
        if (_observedItemMasters.Add(master))
            master.PropertyChanged += OnItemMasterPropertyChanged;
    }

    private void DetachItemMaster(ItemMasterRowVm master)
    {
        if (_observedItemMasters.Remove(master))
            master.PropertyChanged -= OnItemMasterPropertyChanged;
    }

    private void OnItemMasterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ItemMasterRowVm.Name))
            InvalidateItemMasterIndexes();
    }

    private void InvalidateItemMasterIndexes()
    {
        _itemMasterByName = null;
        _quickAdd.InvalidateIndex();
    }

    private void AddTrailingBlankRow(bool deferCollectionRecompute = false)
    {
        if (!deferCollectionRecompute)
        {
            Lines.Add(CreateBlankRow());
            return;
        }

        _deferLineCollectionRecompute = true;
        try
        {
            Lines.Add(CreateBlankRow());
        }
        finally
        {
            _deferLineCollectionRecompute = false;
        }
    }

    private BillLineViewModel CreateBlankRow() => new();

    private void ReplaceLines(IEnumerable<BillLineViewModel> rows)
    {
        _deferLineCollectionRecompute = true;
        try
        {
            Lines.Clear();
            foreach (var row in rows)
                Lines.Add(row);
            AddTrailingBlankRow();
        }
        finally
        {
            _deferLineCollectionRecompute = false;
        }

        Recompute();
    }

    private void RemoveFocusedRow()
    {
        if (Lines.Count <= 1) return;
        var idx = Math.Clamp(FocusedRowIndex, 0, Lines.Count - 1);
        Lines.RemoveAt(idx);
        if (Lines.Count == 0 || !Lines[^1].IsEmpty) AddTrailingBlankRow();
        FocusedRowIndex = Math.Clamp(idx, 0, Lines.Count - 1);
    }

    private void RemoveLine(BillLineViewModel? line)
    {
        if (line is null || Lines.Count <= 1) return;
        var idx = Lines.IndexOf(line);
        if (idx < 0) return;
        Lines.RemoveAt(idx);
        if (Lines.Count == 0 || !Lines[^1].IsEmpty) AddTrailingBlankRow();
        FocusedRowIndex = Math.Clamp(idx, 0, Lines.Count - 1);
    }

    private void Recompute()
    {
        foreach (var line in Lines)
            RecomputeLine(line);

        RecomputeTotalsFromCurrentLines();
    }

    private void RecomputeLine(BillLineViewModel line)
    {
        if (line.IsEmpty)
        {
            line.EffectiveRate = 0m;
            line.LineTotal = 0m;
            return;
        }

        var result = BillCalculator.ComputeLine(InvoicePayloadMapper.BuildCalculatorInputs(
            line,
            Rate24Kt ?? 0m,
            ResolvePurityPercent(line)));
        line.EffectiveRate = result.EffectiveRate;
        line.LineTotal = result.LineTotalInclusive;
    }

    private void RecomputeTotalsFromCurrentLines()
    {
        var (inclusiveTotal, weight, count) = SummarizeCurrentLines();
        var effectiveDiscount = Math.Max(0m, Discount);
        if (effectiveDiscount != Discount)
            SetDiscountFromLinkedTotals(effectiveDiscount);

        var totals = BillCalculator.BuildTotals(inclusiveTotal, effectiveDiscount);
        Subtotal = totals.SubtotalBase;
        Cgst = totals.Cgst;
        Sgst = totals.Sgst;
        RoundOff = totals.RoundOff;
        GrandTotal = totals.GrandTotal;
        SetFinalAmountFromLinkedTotals(totals.GrandTotal);
        ItemCount = count;
        TotalWeight = weight;
        DiscountPercentHint = (effectiveDiscount > 0m && Subtotal > 0m)
            ? $"≈ {(double)(effectiveDiscount / Subtotal) * 100.0:0.0}%"
            : string.Empty;
    }

    private (decimal InclusiveTotal, decimal Weight, int Count) SummarizeCurrentLines()
    {
        decimal inclusiveTotal = 0m;
        decimal weight = 0m;
        int count = 0;

        foreach (var line in Lines)
        {
            if (line.IsEmpty) continue;

            inclusiveTotal += line.LineTotal;
            weight += line.NetWeight;
            count++;
        }

        return (inclusiveTotal, weight, count);
    }

    private void ApplyFinalAmount(decimal requestedFinalAmount)
    {
        var targetGrand = Math.Round(Math.Max(0m, requestedFinalAmount), 0, MidpointRounding.AwayFromZero);
        var (inclusiveTotal, _, _) = SummarizeCurrentLines();
        var undiscounted = BillCalculator.BuildTotals(inclusiveTotal, 0m);

        if (targetGrand >= undiscounted.GrandTotal)
        {
            SetDiscountFromLinkedTotals(0m);
            RecomputeTotalsFromCurrentLines();
            if (targetGrand > undiscounted.GrandTotal && undiscounted.GrandTotal > 0m)
            {
                SaveStatus = $"Final amount cannot exceed ₹{undiscounted.GrandTotal:N0}; discount reset.";
            }
            return;
        }

        var preDiscountTotal = undiscounted.SubtotalBase + undiscounted.Cgst + undiscounted.Sgst;
        var computedDiscount = Math.Round(
            Math.Max(0m, preDiscountTotal - targetGrand),
            2,
            MidpointRounding.AwayFromZero);

        if (!DiscountEnabled)
            SetDiscountEnabledFromLinkedTotals(true);

        SetDiscountFromLinkedTotals(computedDiscount);
        RecomputeTotalsFromCurrentLines();
    }

    private void SetDiscountFromLinkedTotals(decimal value)
    {
        if (Discount == value) return;

        _syncingLinkedTotals = true;
        try
        {
            Discount = value;
        }
        finally
        {
            _syncingLinkedTotals = false;
        }
    }

    private void SetDiscountEnabledFromLinkedTotals(bool value)
    {
        if (DiscountEnabled == value) return;

        _syncingLinkedTotals = true;
        try
        {
            DiscountEnabled = value;
        }
        finally
        {
            _syncingLinkedTotals = false;
        }
    }

    private void SetFinalAmountFromLinkedTotals(decimal value)
    {
        if (FinalAmount == value) return;

        _syncingLinkedTotals = true;
        try
        {
            FinalAmount = value;
        }
        finally
        {
            _syncingLinkedTotals = false;
        }
    }

    private decimal ResolvePurityPercent(BillLineViewModel line)
        => InvoicePayloadMapper.ResolvePurityPercent(line, KaratMasters);

    private bool CanSaveDraft()
        => !IsSaving
           && _billsApi is not null
           && Lines.Any(l => !l.IsEmpty);

    partial void OnIsSavingChanged(bool value) => SaveDraftCommand.NotifyCanExecuteChanged();

    private async Task SaveDraftAsync(CancellationToken cancellationToken)
    {
        if (_billsApi is null) return;

        var validationError = ValidateForSave();
        if (validationError is not null)
        {
            SaveStatus = validationError;
            return;
        }
        RateMissing = false;

        IsSaving = true;
        SaveStatus = _draftBillId is null ? "Saving bill…" : "Updating bill…";
        Guid? savedBillId = null;
        try
        {
            var payload = BuildPayload();

            BillResponse bill;
            if (_draftBillId is null)
            {
                bill = await _billsApi.CreateDraftAsync(new CreateBillDraftRequest(null, payload), cancellationToken);
                _draftBillId = bill.Id;
                OnPropertyChanged(nameof(IsDraftSaved));
            }
            else
            {
                bill = await _billsApi.UpdateDraftAsync(_draftBillId.Value, new UpdateBillDraftRequest(payload), cancellationToken);
            }

            CurrentBillState = bill.State;
            if (!string.IsNullOrWhiteSpace(bill.InvoiceNumber))
                InvoiceNumber = bill.InvoiceNumber!;
            SaveStatus = $"Saved · {bill.State} · {InvoiceNumber}";
            LastSavedAtUtc = DateTimeOffset.UtcNow;
            savedBillId = bill.Id;
        }
        catch (HttpRequestException ex)
        {
            SaveStatus = $"Save failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            SaveStatus = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }

        if (savedBillId is { } id)
        {
            SaveCompletedReadyForPrint?.Invoke(this, id);
        }
    }

    private BillPayloadDto BuildPayload() => InvoicePayloadMapper.BuildPayload(
        Lines,
        BillDate,
        PartyName,
        Narration,
        Payment,
        Rate24Kt,
        Subtotal,
        Discount,
        Cgst,
        Sgst,
        RoundOff,
        GrandTotal);

    private void RefreshQuickAddResults() => _quickAdd.RefreshResults();

    private void CommitQuickAdd(ItemMasterRowVm? master)
    {
        var target = _quickAdd.Commit(master);
        if (target is not null)
            QuickAddCommitted?.Invoke(this, target);
    }

    private void ClearInvoice()
    {
        _draftBillId = null;
        CurrentBillState = null;
        OnPropertyChanged(nameof(IsDraftSaved));
        InvoiceNumber = InvoiceNumberPlaceholder;
        PartyName = string.Empty;
        Narration = string.Empty;
        Discount = 0m;
        DiscountEnabled = false;
        SaveStatus = string.Empty;
        LastSavedAtUtc = null;
        RateMissing = false;
        IsEditingExistingBill = false;
        IsEditingPosted = false;
        ReplaceLines([]);
        _ = RefreshNextNumberAsync();
    }

    public Task RefreshNextNumberAsync(CancellationToken cancellationToken = default)
        => RefreshNextNumberAsync(waitForApi: false, cancellationToken);

    /// <summary>
    /// Fetches the next-invoice-number preview and, if <paramref name="waitForApi"/>
    /// is true, defers the HTTP call until <see cref="IApiReadinessSignal"/>
    /// reports the API child is reachable. The first activation of the Invoice
    /// tab on a cold launch fires before the API child has bound its port; by
    /// waiting up to ~10 s for readiness we (a) make the preview actually land
    /// and (b) pre-pay the cold-pool / EF model-build / TLS handshake cost
    /// before the operator hits Save, so the first SaveDraft of the session
    /// no longer blocks visibly.
    /// </summary>
    public async Task RefreshNextNumberAsync(bool waitForApi, CancellationToken cancellationToken = default)
    {
        if (_numberingApi is null) return;
        if (_draftBillId is not null) return;

        if (waitForApi && _apiReadiness is { IsReady: false })
        {
            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(ApiReadinessWait);
                await _apiReadiness.WhenReadyAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Readiness wait timed out. Fall through and try the request
                // anyway — Save will surface a clearer error than the preview
                // ever could, and the placeholder stays on screen meanwhile.
            }
            catch (OperationCanceledException)
            {
                // External cancellation — caller is no longer interested.
                return;
            }
        }

        try
        {
            var preview = await _numberingApi.GetPreviewAsync(cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(preview.FormattedNumber))
                InvoiceNumber = preview.FormattedNumber;
        }
        catch
        {
            // preview is a hint; keep the placeholder on failure
        }
    }

    public async Task LoadBillForEditAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        if (_billsApi is null) return;

        IsSaving = true;
        SaveStatus = "Loading bill…";
        try
        {
            var bill = await _billsApi.GetAsync(billId, cancellationToken);
            if (bill is null || bill.CurrentRevision is null)
            {
                SaveStatus = "Bill not found.";
                return;
            }
            var payload = bill.CurrentRevision.Payload;

            PartyName = payload.PartyName ?? string.Empty;
            Payment = InvoiceEditMapper.ResolvePayment(payload.Payment, PaymentOptions, Payment);
            BillDate = new DateTimeOffset(payload.BillDate.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset);
            Narration = payload.Notes ?? string.Empty;
            Discount = payload.Totals.DiscountTotal;
            DiscountEnabled = payload.Totals.DiscountTotal > 0m;
            Rate24Kt = payload.Rate24Kt;
            ReplaceLines(InvoiceEditMapper.CreateRows(payload, ItemMasters, KaratMasters));

            _draftBillId = bill.Id;
            CurrentBillState = bill.State;
            IsEditingExistingBill = true;
            IsEditingPosted = !BillStateCapabilities.IsPendingLike(bill.State);
            RateMissing = false;
            if (!string.IsNullOrWhiteSpace(bill.InvoiceNumber))
                InvoiceNumber = bill.InvoiceNumber!;
            OnPropertyChanged(nameof(IsDraftSaved));
            SaveStatus = IsEditingPosted
                ? $"Editing {bill.InvoiceNumber} (was {bill.State}). Save re-queues to Tally."
                : $"Editing {bill.InvoiceNumber ?? "(pending)"}";
        }
        catch (Exception ex)
        {
            SaveStatus = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void PrintPreview()
    {
        // The MainWindowViewModel hooks F9 / Preview to open the Print Preview dialog;
        // this command is kept as a fallback status hint when the preview is unavailable
        // (e.g. bootstrap not finished wiring dialog commands).
        if (ItemCount == 0)
        {
            SaveStatus = "Add a line before previewing the invoice.";
            return;
        }
        SaveStatus = $"Opening invoice preview · Grand ₹{GrandTotal:N0} · {ItemCount} items";
    }

    public BillPrintContent? BuildPrintContent(CompanyProfile company)
    {
        if (ItemCount == 0) return null;
        var payload = BuildPayload();
        return InvoicePayloadMapper.BuildPrintContent(
            string.IsNullOrWhiteSpace(InvoiceNumber) ? InvoiceNumberPlaceholder : InvoiceNumber,
            payload,
            Payment,
            Rate24Kt,
            company,
            BuildKaratMappings());
    }

    private IReadOnlyList<KaratMasterEntry>? BuildKaratMappings()
    {
        if (KaratMasters is null || KaratMasters.Count == 0) return null;
        var entries = new List<KaratMasterEntry>(KaratMasters.Count);
        foreach (var row in KaratMasters)
        {
            if (row.TryBuildEntry(out var entry, out _))
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    private string? ValidateForSave()
    {
        var error = InvoicePayloadMapper.ValidateForSave(Lines, Rate24Kt, out var rateMissing);
        RateMissing = rateMissing;
        return error;
    }
}
