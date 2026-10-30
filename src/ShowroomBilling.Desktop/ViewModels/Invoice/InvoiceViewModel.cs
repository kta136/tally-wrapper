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
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

public partial class InvoiceViewModel : ObservableObject
{
    private readonly IBillsApiClient? _billsApi;
    private readonly INumberingApiClient? _numberingApi;
    private readonly SettingsViewModel? _settings;
    private readonly IApiReadinessSignal? _apiReadiness;
    private readonly BillsAdminTokenGate _adminTokenGate;
    private readonly InvoiceLineCollectionObserver _lineCollectionObserver;
    private readonly HashSet<ItemMasterRowVm> _observedItemMasters = new();
    private bool _deferLineCollectionRecompute;
    private bool _syncingLinkedTotals;
    private Guid? _draftBillId;
    private Guid? _draftRevisionId;
    // Id of the Save that will create this bill. Kept across retries so a save
    // whose response was lost resolves to the bill it already created.
    private Guid? _createOperationId;
    // The bill exactly as the server stored it on the last save; post-save
    // printing uses this, not the live form.
    private BillResponse? _lastSavedBill;
    // Shown until the server assigns a number, including on an estimate
    // printed before the bill is saved. Deliberately not shaped like a real
    // number: the old placeholder carried a prefix and a past fiscal year.
    private const string InvoiceNumberPlaceholder = "Assigned on save";
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
        IApiReadinessSignal? apiReadiness = null,
        AdminTokenStore? adminTokens = null)
    {
        _billsApi = billsApi;
        _numberingApi = numberingApi;
        _settings = settings;
        _apiReadiness = apiReadiness;
        _adminTokenGate = new BillsAdminTokenGate(adminTokens);

        Lines = new ObservableCollection<BillLineViewModel>();
        AddTrailingBlankRow();

        AddRowCommand = new RelayCommand(() => AddTrailingBlankRow(deferCollectionRecompute: true));
        RemoveFocusedRowCommand = new RelayCommand(RemoveFocusedRow, () => Lines.Count > 1);
        RemoveLineCommand = new RelayCommand<BillLineViewModel?>(RemoveLine, line => line is not null && Lines.Count > 1);
        SaveDraftCommand = new AsyncRelayCommand(SaveDraftAsync, CanSaveDraft);
        ClearCommand = new RelayCommand(ClearInvoice, HasAnythingToClear);
        NewBillCommand = new AsyncRelayCommand(StartNewBillAsync, () => !IsSaving && HasAnythingToClear());
        _lineCollectionObserver = new InvoiceLineCollectionObserver(
            Lines,
            OnLineMutated,
            OnLineCollectionChanged);
        QuickAddResults = new ObservableCollection<ItemMasterRowVm>();
        QuickAddCommitCommand = new RelayCommand<ItemMasterRowVm?>(CommitQuickAdd);
        OpenQuickAddCommand = new RelayCommand(() => IsQuickAddOpen = true);
        CloseQuickAddCommand = new RelayCommand(() => IsQuickAddOpen = false);
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

    /// <summary>
    /// The New bill button and Ctrl+Shift+N: leaves the bill on the form and starts an
    /// empty one, asking first when that would throw away something not saved.
    /// </summary>
    public IAsyncRelayCommand NewBillCommand { get; }
    public IRelayCommand<ItemMasterRowVm?> QuickAddCommitCommand { get; }
    public IRelayCommand OpenQuickAddCommand { get; }
    public IRelayCommand CloseQuickAddCommand { get; }
    public ObservableCollection<ItemMasterRowVm> QuickAddResults { get; }

    [ObservableProperty] private string quickAddQuery = string.Empty;

    /// <summary>"10 items" while the search box is empty, "6 matches for “22kt”" once it is not.</summary>
    [ObservableProperty] private string quickAddCountText = string.Empty;
    [ObservableProperty] private ItemMasterRowVm? quickAddSelection;

    partial void OnQuickAddQueryChanged(string value) => RefreshQuickAddResults();

    [ObservableProperty] private string saveStatus = string.Empty;
    [ObservableProperty] private DateTimeOffset? lastSavedAtUtc;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? currentBillState;
    [ObservableProperty] private bool rateMissing;
    [ObservableProperty] private bool isEditingPosted;

    /// <summary>
    /// Handler the shell wires up to open the admin-unlock dialog and wait
    /// until it closes. Used when the bill being saved is posted.
    /// </summary>
    public Func<CancellationToken, Task>? AdminUnlockHandler
    {
        get => _adminTokenGate.AdminUnlockHandler;
        set => _adminTokenGate.AdminUnlockHandler = value;
    }

    /// <summary>
    /// Asks the operator a yes/no question; the shell wires it to the confirm overlay.
    /// With none wired the answer is "no".
    /// </summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    public event EventHandler<Guid>? SaveCompletedReadyForPrint;
    public event EventHandler<BillLineViewModel>? QuickAddCommitted;

    /// <summary>Raised when <see cref="NewBillCommand"/> has emptied the form, so the view can put the cursor back at the top.</summary>
    public event EventHandler? NewBillStarted;

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
    [ObservableProperty] private string payment = PaymentMode.Cash;
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

    [ObservableProperty] private int focusedRowIndex;

    /// <summary>Today's 24K, 22K and 18K rates per gram, or null until a 24K rate is typed.</summary>
    [ObservableProperty] private RateLadder? rates;

    /// <summary>How the focused line was priced, for the strip under the table; null while that line is blank.</summary>
    [ObservableProperty] private LineExplanation? focusExplanation;

    /// <summary>The weight columns added up, grams and carats apart.</summary>
    [ObservableProperty] private LineWeightTotals weightTotals = LineWeightTotals.Empty;

    /// <summary>The sum of every line total, GST included: the figure at the foot of the Line total column.</summary>
    [ObservableProperty] private decimal inclusiveTotal;

    /// <summary>Whether the Quick add panel is open beside the table, or folded to its rail.</summary>
    [ObservableProperty] private bool isQuickAddOpen;

    /// <summary>What the bar beside Save bill says about the bill: new, draft, saved, or what went wrong.</summary>
    [ObservableProperty] private string statusText = "New bill";

    /// <summary>"ok", "warn", "err" or "neutral": the colour of the dot before <see cref="StatusText"/>.</summary>
    [ObservableProperty] private string statusTone = "neutral";

    private bool _hasUnsavedChanges;
    private bool _suspendDirtyTracking;

    partial void OnFocusedRowIndexChanged(int value) => UpdateFocusExplanation();

    /// <summary>False while the box still shows the "Assigned on save" hint, so it can be drawn as one.</summary>
    public bool IsInvoiceNumberAssigned => InvoiceNumber != InvoiceNumberPlaceholder;

    partial void OnInvoiceNumberChanged(string value) => OnPropertyChanged(nameof(IsInvoiceNumberAssigned));
    partial void OnSaveStatusChanged(string value) => UpdateStatus();
    partial void OnPartyNameChanged(string value)
    {
        MarkChanged();
        RaiseCanClearChanged();
    }

    partial void OnNarrationChanged(string value)
    {
        MarkChanged();
        RaiseCanClearChanged();
    }

    partial void OnPaymentChanged(string value) => MarkChanged();
    partial void OnCurrentBillStateChanged(string? value)
    {
        UpdateStatus();
        RaiseCanClearChanged();
        OnPropertyChanged(nameof(NewBillLabel));
    }

    partial void OnItemCountChanged(int value)
    {
        UpdateStatus();
        RaiseCanClearChanged();
    }

    /// <summary>
    /// What the button beside Print estimate says: "Clear" while the form holds a bill not
    /// saved yet, "New bill" while it holds a saved one, which the button leaves as it is.
    /// </summary>
    public string NewBillLabel => CurrentBillState is null ? "Clear" : "New bill";

    /// <summary>Clear has nothing to do on an untouched bill, so the button is greyed.</summary>
    private bool HasAnythingToClear()
        => ItemCount > 0
           || _draftBillId is not null
           || !string.IsNullOrWhiteSpace(PartyName)
           || !string.IsNullOrWhiteSpace(Narration);

    private void RaiseCanClearChanged()
    {
        ClearCommand.NotifyCanExecuteChanged();
        NewBillCommand.NotifyCanExecuteChanged();
    }

    partial void OnRate24KtChanged(decimal? value)
    {
        if (value is > 0m) RateMissing = false;
        RefreshRates();
        MarkChanged();
        Recompute();
    }
    partial void OnDiscountChanged(decimal value)
    {
        if (_syncingLinkedTotals) return;
        MarkChanged();

        if (value < 0m)
        {
            SetFromLinkedTotals(() => Discount = 0m);
            SaveStatus = "Discount cannot be negative.";
            RecomputeTotalsFromCurrentLines();
            return;
        }

        if (value > 0m && !DiscountEnabled)
        {
            SetFromLinkedTotals(() => DiscountEnabled = true);
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
        MarkChanged();

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
        var master = ItemMasters?.FirstOrDefault(
            candidate => string.Equals(candidate.Name?.Trim(), typed, StringComparison.OrdinalIgnoreCase));
        if (master is not null)
        {
            line.ItemMaster = master;
        }
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

        RefreshQuickAddResults();
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
            RefreshQuickAddResults();
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

    /// <summary>Re-reads today's rates; the shell calls it when the karat masters change.</summary>
    public void RefreshRates() => Rates = RateLadder.For(Rate24Kt, KaratMasters);

    private void UpdateFocusExplanation()
    {
        var line = Lines.Count == 0 ? null : Lines[Math.Clamp(FocusedRowIndex, 0, Lines.Count - 1)];
        FocusExplanation = line is null || line.IsEmpty
            ? null
            : LineExplainer.Describe(line, Rate24Kt ?? 0m, ResolvePurityPercent(line));
    }

    private void MarkChanged()
    {
        if (_suspendDirtyTracking) return;
        _hasUnsavedChanges = true;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (IsSaving)
        {
            (StatusText, StatusTone) = (string.IsNullOrWhiteSpace(SaveStatus) ? "Saving…" : SaveStatus, "neutral");
        }
        else if (!string.IsNullOrWhiteSpace(SaveStatus) && !(_hasUnsavedChanges && IsSavedMessage(SaveStatus)))
        {
            (StatusText, StatusTone) = (SaveStatus, ToneOf(SaveStatus));
        }
        else if (_hasUnsavedChanges && (_draftBillId is not null || ItemCount > 0 || !string.IsNullOrWhiteSpace(PartyName)))
        {
            (StatusText, StatusTone) = (_draftBillId is null ? "Draft, not saved" : "Unsaved changes", "warn");
        }
        else if (_draftBillId is not null)
        {
            (StatusText, StatusTone) = ($"Saved · {InvoiceNumber}", "ok");
        }
        else
        {
            (StatusText, StatusTone) = ("New bill", "neutral");
        }
    }

    private static bool IsSavedMessage(string text)
        => text.StartsWith("Saved", StringComparison.Ordinal) || text.StartsWith("Printed", StringComparison.Ordinal);

    /// <summary>Colour of a status message: done is green, in progress is grey, anything else needs a look.</summary>
    internal static string ToneOf(string text)
    {
        if (IsSavedMessage(text)) return "ok";
        if (text.EndsWith('…') || text.StartsWith("Editing", StringComparison.Ordinal)) return "neutral";
        return "err";
    }

    private void RemoveFocusedRow()
    {
        if (Lines.Count <= 1) return;
        RemoveRowAt(Math.Clamp(FocusedRowIndex, 0, Lines.Count - 1));
    }

    private void RemoveLine(BillLineViewModel? line)
    {
        if (line is null || Lines.Count <= 1) return;
        var idx = Lines.IndexOf(line);
        if (idx < 0) return;
        RemoveRowAt(idx);
    }

    // The grid always ends in one blank row, and focus stays where the removed row was.
    private void RemoveRowAt(int index)
    {
        Lines.RemoveAt(index);
        if (Lines.Count == 0 || !Lines[^1].IsEmpty) AddTrailingBlankRow();
        FocusedRowIndex = Math.Clamp(index, 0, Lines.Count - 1);
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
        var (inclusiveTotal, count) = SummarizeCurrentLines();
        InclusiveTotal = inclusiveTotal;
        WeightTotals = LineWeightTotals.Of(Lines);
        // Discount is money: keep it to paise so the saved totals and the
        // Tally voucher legs balance exactly.
        var effectiveDiscount = Math.Round(Math.Max(0m, Discount), 2, MidpointRounding.AwayFromZero);
        if (effectiveDiscount != Discount)
            SetFromLinkedTotals(() => Discount = effectiveDiscount);

        var totals = BillCalculator.BuildTotals(inclusiveTotal, effectiveDiscount);
        Subtotal = totals.SubtotalBase;
        Cgst = totals.Cgst;
        Sgst = totals.Sgst;
        RoundOff = totals.RoundOff;
        GrandTotal = totals.GrandTotal;
        SetFromLinkedTotals(() => FinalAmount = totals.GrandTotal);
        ItemCount = count;
        DiscountPercentHint = (effectiveDiscount > 0m && Subtotal > 0m)
            ? $"{(double)(effectiveDiscount / Subtotal) * 100.0:0.0}%"
            : string.Empty;
        UpdateFocusExplanation();
    }

    private (decimal InclusiveTotal, int Count) SummarizeCurrentLines()
    {
        decimal inclusiveTotal = 0m;
        int count = 0;

        foreach (var line in Lines)
        {
            if (line.IsEmpty) continue;

            inclusiveTotal += line.LineTotal;
            count++;
        }

        return (inclusiveTotal, count);
    }

    private void ApplyFinalAmount(decimal requestedFinalAmount)
    {
        var targetGrand = Math.Round(Math.Max(0m, requestedFinalAmount), 0, MidpointRounding.AwayFromZero);
        var (inclusiveTotal, _) = SummarizeCurrentLines();
        var undiscounted = BillCalculator.BuildTotals(inclusiveTotal, 0m);

        if (targetGrand >= undiscounted.GrandTotal)
        {
            SetFromLinkedTotals(() => Discount = 0m);
            RecomputeTotalsFromCurrentLines();
            if (targetGrand > undiscounted.GrandTotal && undiscounted.GrandTotal > 0m)
            {
                SaveStatus = $"Final amount cannot exceed ₹{undiscounted.GrandTotal:N0}; discount reset.";
            }
            return;
        }

        var computedDiscount = BillCalculator.FinalAmountDiscount(inclusiveTotal, targetGrand);

        if (!DiscountEnabled)
            SetFromLinkedTotals(() => DiscountEnabled = true);

        SetFromLinkedTotals(() => Discount = computedDiscount);
        RecomputeTotalsFromCurrentLines();
    }

    /// <summary>
    /// Discount, its tick box and the final amount drive each other. A value
    /// written here is the result of that link, so its own change handler
    /// must not start the calculation again.
    /// </summary>
    private void SetFromLinkedTotals(Action assign)
    {
        _syncingLinkedTotals = true;
        try
        {
            assign();
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

    partial void OnIsSavingChanged(bool value)
    {
        SaveDraftCommand.NotifyCanExecuteChanged();
        // Emptying the form while a save or a load is in flight would let its
        // answer land on the new bill.
        NewBillCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

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

        // The server refuses an edit to a posted bill without an admin session.
        string? adminToken = null;
        if (_draftBillId is not null && BillStateCapabilities.IsPosted(CurrentBillState))
        {
            (adminToken, _) = await _adminTokenGate.EnsureAsync(cancellationToken);
            if (adminToken is null)
            {
                SaveStatus = "Not saved: this bill is posted, and saving an edit to it needs admin unlock.";
                return;
            }
        }

        IsSaving = true;
        SaveStatus = _draftBillId is null ? "Saving bill…" : "Updating bill…";
        Guid? savedBillId = null;
        try
        {
            var payload = BuildPayload();

            BillResponse bill;
            if (_draftBillId is null)
            {
                _createOperationId ??= Guid.NewGuid();
                bill = await _billsApi.CreateDraftAsync(
                    new CreateBillDraftRequest(null, payload, _createOperationId),
                    cancellationToken);
                _draftBillId = bill.Id;
                _createOperationId = null;
            }
            else
            {
                bill = await _billsApi.UpdateDraftAsync(
                    _draftBillId.Value,
                    new UpdateBillDraftRequest(payload, _draftRevisionId),
                    adminToken,
                    cancellationToken);
            }
            _draftRevisionId = bill.CurrentRevisionId;

            CurrentBillState = bill.State;
            if (!string.IsNullOrWhiteSpace(bill.InvoiceNumber))
                InvoiceNumber = bill.InvoiceNumber!;
            _hasUnsavedChanges = false;
            SaveStatus = $"Saved · {bill.State} · {InvoiceNumber}";
            LastSavedAtUtc = DateTimeOffset.UtcNow;
            savedBillId = bill.Id;
            _lastSavedBill = bill;
        }
        catch (Exception ex)
        {
            SaveStatus = $"Save failed: {ApiResponseReader.FormatError(ex)}";
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

    private void RefreshQuickAddResults()
    {
        var query = (QuickAddQuery ?? string.Empty).Trim();
        var matches = (ItemMasters ?? [])
            .Where(master => !string.IsNullOrWhiteSpace(master.Name)
                && (query.Length == 0 || master.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(50)
            .ToList();

        QuickAddResults.Clear();
        foreach (var match in matches)
            QuickAddResults.Add(match);

        var total = (ItemMasters ?? []).Count(master => !string.IsNullOrWhiteSpace(master.Name));
        QuickAddCountText = query.Length == 0
            ? (total == 1 ? "1 item" : $"{total} items")
            : $"{matches.Count} {(matches.Count == 1 ? "match" : "matches")} for “{query}”";

        QuickAddSelection = QuickAddResults.FirstOrDefault();
    }

    /// <summary>Puts the picked item on the first empty line, adding one if every line is in use.</summary>
    private void CommitQuickAdd(ItemMasterRowVm? master)
    {
        master ??= QuickAddSelection ?? QuickAddResults.FirstOrDefault();
        if (master is null) return;

        var target = Lines.FirstOrDefault(line => line.IsEmpty);
        if (target is null)
        {
            target = CreateBlankRow();
            Lines.Add(target);
        }

        target.ItemMaster = master;
        QuickAddQuery = string.Empty;
        QuickAddCommitted?.Invoke(this, target);
    }

    private async Task StartNewBillAsync()
    {
        if (_hasUnsavedChanges && !await ConfirmDiscardAsync()) return;

        ClearInvoice();
        NewBillStarted?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (ConfirmHandler is null) return false;

        var question = _draftBillId is null
            ? new ConfirmRequest(
                "Discard this bill?",
                "It has not been saved, so its lines, party and notes are lost.",
                "Discard bill",
                IsWarning: true)
            : new ConfirmRequest(
                $"Discard your changes to {InvoiceNumber}?",
                "The bill stays as it was last saved, and the form starts a new bill.",
                "Discard changes",
                IsWarning: true);
        return await ConfirmHandler(question);
    }

    private void ClearInvoice()
    {
        _suspendDirtyTracking = true;
        try
        {
            ResetForNewBill();
        }
        finally
        {
            _suspendDirtyTracking = false;
        }

        _hasUnsavedChanges = false;
        UpdateStatus();
        _ = RefreshNextNumberAsync();
    }

    private void ResetForNewBill()
    {
        _draftBillId = null;
        _draftRevisionId = null;
        _createOperationId = null;
        _lastSavedBill = null;
        CurrentBillState = null;
        InvoiceNumber = InvoiceNumberPlaceholder;
        // The VM is a singleton: without this, a new bill keeps the date and
        // payment mode of the bill edited before it, or yesterday's date on a
        // counter left open overnight.
        BillDate = DateTimeOffset.Now;
        Payment = PaymentMode.Cash;
        PartyName = string.Empty;
        Narration = string.Empty;
        Discount = 0m;
        DiscountEnabled = false;
        SaveStatus = string.Empty;
        LastSavedAtUtc = null;
        RateMissing = false;
        IsEditingPosted = false;
        ReplaceLines([]);
    }

    public Task RefreshNextNumberAsync(CancellationToken cancellationToken = default)
        => RefreshNextNumberAsync(waitForApi: false, cancellationToken);

    /// <summary>
    /// Fetches the next-invoice-number preview and, if <paramref name="waitForApi"/>
    /// is true, defers the HTTP call until <see cref="IApiReadinessSignal"/>
    /// reports the API is reachable. The first activation of the Invoice
    /// tab on a cold launch can fire before the API has answered; by
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
            // The server numbers a bill in its bill date's fiscal year.
            var fiscalYear = InvoiceNumberFormatter.ComputeFiscalYear(DateOnly.FromDateTime(BillDate.LocalDateTime));
            var preview = await _numberingApi.GetPreviewAsync(fiscalYear: fiscalYear, cancellationToken: cancellationToken);
            // A bill may have been loaded for edit (or saved) while the preview
            // was in flight; its real number must not be replaced by the next one.
            if (_draftBillId is not null) return;
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

            _suspendDirtyTracking = true;
            PartyName = payload.PartyName ?? string.Empty;
            Payment = InvoiceEditMapper.ResolvePayment(payload.Payment, PaymentOptions, Payment);
            BillDate = new DateTimeOffset(payload.BillDate.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset);
            Narration = payload.Notes ?? string.Empty;
            Discount = payload.Totals.DiscountTotal;
            DiscountEnabled = payload.Totals.DiscountTotal > 0m;
            Rate24Kt = payload.Rate24Kt;
            ReplaceLines(InvoiceEditMapper.CreateRows(payload, ItemMasters, KaratMasters));
            _suspendDirtyTracking = false;
            _hasUnsavedChanges = false;

            _draftBillId = bill.Id;
            _draftRevisionId = bill.CurrentRevisionId;
            _lastSavedBill = null;
            CurrentBillState = bill.State;
            IsEditingPosted = !BillStateCapabilities.IsPendingLike(bill.State);
            RateMissing = false;
            if (!string.IsNullOrWhiteSpace(bill.InvoiceNumber))
                InvoiceNumber = bill.InvoiceNumber!;
            SaveStatus = IsEditingPosted
                ? $"Editing {bill.InvoiceNumber} (was {bill.State}). Save re-queues to Tally."
                : $"Editing {bill.InvoiceNumber ?? "(pending)"}";
        }
        catch (Exception ex)
        {
            _suspendDirtyTracking = false;
            SaveStatus = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
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

    /// <summary>
    /// Print content for the bill as it was last saved. Anything typed into
    /// the form while the save was in flight was not saved, so it must not be
    /// printed as if it were. Falls back to the live form if nothing was saved.
    /// </summary>
    public BillPrintContent? BuildSavedPrintContent(CompanyProfile company)
    {
        if (_lastSavedBill?.CurrentRevision?.Payload is not { } payload)
        {
            return BuildPrintContent(company);
        }

        return InvoicePayloadMapper.BuildPrintContent(
            _lastSavedBill.InvoiceNumber ?? InvoiceNumberPlaceholder,
            payload,
            payload.Payment ?? Payment,
            payload.Rate24Kt,
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
