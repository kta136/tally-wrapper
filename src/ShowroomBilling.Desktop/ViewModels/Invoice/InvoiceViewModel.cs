using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net;
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
    private readonly IBillsApiClient _billsApi;
    private readonly INumberingApiClient _numberingApi;
    private readonly SettingsViewModel? _settings;
    private readonly IDialogService _dialogs;
    private readonly BillsAdminTokenGate _adminTokenGate;
    private readonly InvoiceLineCollectionObserver _lineCollectionObserver;
    // True while a newly published catalogue is being put under the lines: what the
    // item and karat cells do to a line meanwhile is not the operator's doing.
    private bool _rebindingMasters;
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

    public InvoiceViewModel(
        IBillsApiClient billsApi,
        INumberingApiClient numberingApi,
        IDialogService dialogs,
        SettingsViewModel? settings,
        AdminTokenStore? adminTokens = null)
    {
        _billsApi = billsApi;
        _numberingApi = numberingApi;
        _dialogs = dialogs;
        _settings = settings;
        _adminTokenGate = new BillsAdminTokenGate(adminTokens, dialogs);

        Lines = new ObservableCollection<BillLineViewModel>();
        AddTrailingBlankRow();

        _lineCollectionObserver = new InvoiceLineCollectionObserver(
            Lines,
            OnLineMutated,
            OnLineCollectionChanged);
        QuickAddResults = new ObservableCollection<ItemMasterEntry>();
        if (_settings is not null)
        {
            _settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.Catalog))
                    OnCatalogPublished();
            };
        }

        RefreshQuickAddResults();

        Recompute();
    }

    public ObservableCollection<BillLineViewModel> Lines { get; }

    public bool HasMultipleRows => Lines.Count > 1;

    public ObservableCollection<ItemMasterEntry> QuickAddResults { get; }

    [ObservableProperty] private string quickAddQuery = string.Empty;

    /// <summary>"10 items" while the search box is empty, "6 matches for “22kt”" once it is not.</summary>
    [ObservableProperty] private string quickAddCountText = string.Empty;
    [ObservableProperty] private ItemMasterEntry? quickAddSelection;

    partial void OnQuickAddQueryChanged(string value) => RefreshQuickAddResults();

    [ObservableProperty] private string saveStatus = string.Empty;
    [ObservableProperty] private DateTimeOffset? lastSavedAtUtc;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveDraftCommand), nameof(NewBillCommand))]
    private bool isSaving;

    [ObservableProperty] private string? currentBillState;
    [ObservableProperty] private bool rateMissing;
    [ObservableProperty] private bool isEditingPosted;

    public event EventHandler<Guid>? SaveCompletedReadyForPrint;
    public event EventHandler<BillLineViewModel>? QuickAddCommitted;

    /// <summary>Raised when <see cref="NewBillCommand"/> has emptied the form, so the view can put the cursor back at the top.</summary>
    public event EventHandler? NewBillStarted;

    /// <summary>The saved items and karats. A line never sees the Settings tab's own rows, which may be half typed.</summary>
    private MasterCatalog Catalog => _settings?.Catalog ?? MasterCatalog.Empty;
    public IReadOnlyList<ItemMasterEntry> ItemMasters => Catalog.Items;
    public IReadOnlyList<KaratMasterEntry> KaratMasters => Catalog.Karats;
    public IReadOnlyList<string> PaymentOptions { get; } = [PaymentMode.CreditDebit, PaymentMode.Cash];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvoiceNumberAssigned))]
    private string invoiceNumber = InvoiceNumberPlaceholder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BillDateLocal))]
    private DateTimeOffset billDate = DateTimeOffset.Now;

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

    [ObservableProperty] private string payment = PaymentMode.Cash;
    [ObservableProperty] private decimal? rate24Kt;
    [ObservableProperty] private decimal discount;
    [ObservableProperty] private bool discountEnabled;
    [ObservableProperty] private string discountPercentHint = string.Empty;

    /// <summary>Charged to the customer for paying through the bank, added after GST. Typed on each bill.</summary>
    [ObservableProperty] private decimal bankCharges;
    [ObservableProperty] private bool bankChargesEnabled;
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

    // The colour that came with the status text (ShowStatus). Null when the
    // text was set on its own and the colour is worked out from its wording.
    private string? _saveStatusTone;
    private string? _toneForNextStatus;

    /// <summary>
    /// Sets the status line's text together with its colour ("ok", "warn",
    /// "err" or "neutral"), for a message whose opening word does not say
    /// which it is: "Saved · … · direct print failed" is not a green message.
    /// </summary>
    public void ShowStatus(string text, string tone)
    {
        _toneForNextStatus = tone;
        SaveStatus = text;
        _toneForNextStatus = null;
        if (_saveStatusTone != tone)
        {
            // The text was already this one, so no change was raised for it.
            _saveStatusTone = tone;
            UpdateStatus();
        }
    }

    partial void OnSaveStatusChanged(string value)
    {
        _saveStatusTone = _toneForNextStatus;
        UpdateStatus();
    }
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

    private bool CanStartNewBill => !IsSaving && HasAnythingToClear();

    [RelayCommand(CanExecute = nameof(HasAnythingToClear))]
    private void Clear() => ClearInvoice();

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

    partial void OnBankChargesChanged(decimal value)
    {
        if (_syncingLinkedTotals) return;
        MarkChanged();

        if (value < 0m)
        {
            SetFromLinkedTotals(() => BankCharges = 0m);
            SaveStatus = "Bank charges cannot be negative.";
            RecomputeTotalsFromCurrentLines();
            return;
        }

        if (value > 0m && !BankChargesEnabled)
        {
            SetFromLinkedTotals(() => BankChargesEnabled = true);
        }

        RecomputeTotalsFromCurrentLines();
    }

    partial void OnBankChargesEnabledChanged(bool value)
    {
        if (_syncingLinkedTotals) return;
        if (!value) BankCharges = 0m;
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
        // A removed row takes its fault with it, and the rows after it are numbered again.
        if (_refusedBy is not null)
        {
            RecheckRefusal();
            UpdateStatus();
        }
    }

    private void OnLineMutated(object? sender, EventArgs e)
    {
        // OnCatalogPublished recomputes once it has every line where it should be.
        if (_rebindingMasters) return;

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

        if (Catalog.FindItem(line.ItemName) is { } master)
        {
            line.ItemMaster = master;
        }
    }

    /// <summary>
    /// Settings were loaded or saved and the items or karats are not what they were. Each line is
    /// pointed at the new entry of the item and karat it had, by name, and keeps everything on it:
    /// an item whose default wastage was changed in Settings does not put that over what was typed
    /// on an open bill. The lists are swapped first, because the item and karat cells drop a
    /// selection that is no longer in their list; the lines are put right after that.
    /// </summary>
    private void OnCatalogPublished()
    {
        var catalog = Catalog;
        var held = Lines
            .Select(line => (Line: line, Item: line.ItemMaster?.Name, Karat: line.KaratMaster?.Label))
            .ToList();

        _rebindingMasters = true;
        try
        {
            OnPropertyChanged(nameof(ItemMasters));
            OnPropertyChanged(nameof(KaratMasters));
            foreach (var (line, item, karat) in held)
            {
                // A karat is also found by the line's own text: a bill opened before the karats
                // had loaded has the text and no entry yet, and would count as an unknown karat.
                line.RebindMasters(catalog.FindItem(item), catalog.FindKarat(karat ?? line.Karat));
            }
        }
        finally
        {
            _rebindingMasters = false;
        }

        RefreshQuickAddResults();
        RefreshRates();
        // A karat's purity may be another now.
        Recompute();
        // And an item or karat the bill was refused for may be a saved one now, or the other way.
        if (_refusedBy is not null)
        {
            RecheckRefusal();
            UpdateStatus();
        }
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
        RecheckRefusal();
        UpdateStatus();
    }

    // The check a refused Save or Print estimate failed, while its sentence stands beside Save.
    private Func<string?>? _refusedBy;

    /// <summary>
    /// Says beside Save why Save or Print estimate did not go ahead. The sentence is then kept true:
    /// each edit runs <paramref name="check"/> again, so it goes when the fault is corrected (it
    /// used to stand there until the next Save) and changes if another fault is next.
    /// </summary>
    private void Refuse(string why, Func<string?> check)
    {
        _refusedBy = check;
        SaveStatus = why;
    }

    private void RecheckRefusal()
    {
        if (_refusedBy is null)
        {
            return;
        }

        var still = _refusedBy();
        if (still is null)
        {
            _refusedBy = null;
        }

        SaveStatus = still ?? string.Empty;
    }

    /// <summary>
    /// Print estimate asks this first: true when the estimate must not be made, and the reason is
    /// then beside Save. An estimate with a line priced with no gold in it must not reach a customer.
    /// </summary>
    public bool RefuseEstimateIfNotPriceable()
    {
        if (UnknownKaratProblem() is not { } problem)
        {
            return false;
        }

        Refuse(problem, UnknownKaratProblem);
        return true;
    }

    private void UpdateStatus()
    {
        if (IsSaving)
        {
            (StatusText, StatusTone) = (string.IsNullOrWhiteSpace(SaveStatus) ? "Saving…" : SaveStatus, "neutral");
        }
        else if (!string.IsNullOrWhiteSpace(SaveStatus) && !(_hasUnsavedChanges && IsSavedMessage(SaveStatus)))
        {
            (StatusText, StatusTone) = (SaveStatus, _saveStatusTone ?? ToneOf(SaveStatus));
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

    [RelayCommand]
    private void AddRow() => AddTrailingBlankRow(deferCollectionRecompute: true);

    [RelayCommand(CanExecute = nameof(HasMultipleRows))]
    private void RemoveFocusedRow()
    {
        if (Lines.Count <= 1) return;
        RemoveRowAt(Math.Clamp(FocusedRowIndex, 0, Lines.Count - 1));
    }

    private bool CanRemoveLine(BillLineViewModel? line) => line is not null && Lines.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemoveLine))]
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
            line.KaratUnknown = false;
            return;
        }

        // A karat that is typed but is none of the saved ones has no purity: the line would be
        // priced, and saved, with no gold in it. With no karats saved at all there is nothing to
        // hold the text against.
        line.KaratUnknown = line.IsGoldLine
            && !string.IsNullOrWhiteSpace(line.Karat)
            && KaratMasters.Count > 0
            && line.KaratMaster is null
            && Catalog.FindKarat(line.Karat.Trim()) is null;

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
        var effectiveBankCharges = EffectiveBankCharges();
        if (effectiveBankCharges != BankCharges)
            SetFromLinkedTotals(() => BankCharges = effectiveBankCharges);

        var totals = BillCalculator.BuildTotals(inclusiveTotal, effectiveDiscount, effectiveBankCharges);
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

    /// <summary>Bank charges are money too: to the paisa, and never below none.</summary>
    private decimal EffectiveBankCharges()
        => Math.Round(Math.Max(0m, BankCharges), 2, MidpointRounding.AwayFromZero);

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
        // The final amount is what the customer pays, bank charges included.
        var bankCharges = EffectiveBankCharges();
        var undiscounted = BillCalculator.BuildTotals(inclusiveTotal, 0m, bankCharges);

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

        var computedDiscount = BillCalculator.FinalAmountDiscount(inclusiveTotal, targetGrand, bankCharges);

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
        => !IsSaving && Lines.Any(l => !l.IsEmpty);

    // Emptying the form while a save or a load is in flight would let its answer land on
    // the new bill, so New bill is off while saving, as Save is.
    partial void OnIsSavingChanged(bool value) => UpdateStatus();

    [RelayCommand(CanExecute = nameof(CanSaveDraft))]
    private async Task SaveDraftAsync(CancellationToken cancellationToken)
    {
        var validationError = ValidateForSave();
        if (validationError is not null)
        {
            Refuse(validationError, ValidateForSave);
            return;
        }
        _refusedBy = null;
        RateMissing = false;

        if (ItemsNotSaved() is { Count: > 0 } asTyped)
        {
            // The question is an overlay, and the bill behind it can still change while it is open
            // (a row shortcut, settings arriving that reprice a line). Only the bill the operator
            // said yes to is saved.
            var asked = PayloadText();
            if (!await _dialogs.ConfirmAsync(ItemsAsTypedQuestion(asTyped)))
            {
                // Kept true as the lines change: it goes once each item is picked from the list.
                Refuse(ItemsNotSavedSentence() ?? string.Empty, ItemsNotSavedSentence);
                return;
            }

            if (ValidateForSave() is { } late)
            {
                Refuse(late, ValidateForSave);
                return;
            }

            if (PayloadText() != asked)
            {
                SaveStatus = "Not saved: the bill changed while the question was open. Check it and save again.";
                return;
            }
        }

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
                // An id left from an earlier Save means that Save got no usable
                // answer: it may or may not have reached the server.
                var earlierSaveMayHaveLanded = _createOperationId is not null;
                _createOperationId ??= Guid.NewGuid();
                try
                {
                    bill = await _billsApi.CreateDraftAsync(
                        new CreateBillDraftRequest(payload, _createOperationId),
                        cancellationToken);
                }
                catch (ApiException ex) when (earlierSaveMayHaveLanded && ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // It had landed: the bill exists, holding what the form held
                    // then, and the form has been changed since. The server
                    // will not create it again with other contents, so every
                    // later Save was refused until the form was emptied. Take
                    // that bill as the one being edited and save onto it.
                    var earlier = await _billsApi.GetByOperationAsync(_createOperationId.Value, cancellationToken);
                    if (earlier is null)
                    {
                        throw;
                    }

                    _draftBillId = earlier.Id;
                    _draftRevisionId = earlier.CurrentRevisionId;
                    _createOperationId = null;
                    CurrentBillState = earlier.State;
                    if (!string.IsNullOrWhiteSpace(earlier.InvoiceNumber))
                        InvoiceNumber = earlier.InvoiceNumber!;
                    bill = await _billsApi.UpdateDraftAsync(
                        earlier.Id,
                        new UpdateBillDraftRequest(payload, earlier.CurrentRevisionId),
                        adminToken,
                        cancellationToken);
                }

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
        GrandTotal,
        BankCharges);

    private void RefreshQuickAddResults()
    {
        var query = (QuickAddQuery ?? string.Empty).Trim();
        var matches = ItemMasters
            .Where(master => !string.IsNullOrWhiteSpace(master.Name)
                && (query.Length == 0 || master.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(50)
            .ToList();

        QuickAddResults.Clear();
        foreach (var match in matches)
            QuickAddResults.Add(match);

        var total = ItemMasters.Count(master => !string.IsNullOrWhiteSpace(master.Name));
        QuickAddCountText = query.Length == 0
            ? (total == 1 ? "1 item" : $"{total} items")
            : $"{matches.Count} {(matches.Count == 1 ? "match" : "matches")} for “{query}”";

        QuickAddSelection = QuickAddResults.FirstOrDefault();
    }

    /// <summary>Puts the picked item on the first empty line, adding one if every line is in use.</summary>
    [RelayCommand]
    private void OpenQuickAdd() => IsQuickAddOpen = true;

    [RelayCommand]
    private void CloseQuickAdd() => IsQuickAddOpen = false;

    [RelayCommand]
    private void QuickAddCommit(ItemMasterEntry? master)
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

    /// <summary>
    /// The New bill button and Ctrl+Shift+N: leaves the bill on the form and starts an
    /// empty one, asking first when that would throw away something not saved.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartNewBill))]
    private async Task NewBillAsync()
    {
        if (_hasUnsavedChanges && !await ConfirmDiscardAsync()) return;

        ClearInvoice();
        NewBillStarted?.Invoke(this, EventArgs.Empty);
    }

    private Task<bool> ConfirmDiscardAsync()
    {
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
        return _dialogs.ConfirmAsync(question);
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
        _refusedBy = null;
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
        BankCharges = 0m;
        BankChargesEnabled = false;
        SaveStatus = string.Empty;
        LastSavedAtUtc = null;
        RateMissing = false;
        IsEditingPosted = false;
        ReplaceLines([]);
    }

    /// <summary>
    /// Fetches the number the next bill would get, to show in place of the
    /// "Assigned on save" hint. It is only a hint: a failure leaves the hint.
    /// </summary>
    public async Task RefreshNextNumberAsync(CancellationToken cancellationToken = default)
    {
        if (_draftBillId is not null) return;

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

            _refusedBy = null;
            _suspendDirtyTracking = true;
            PartyName = payload.PartyName ?? string.Empty;
            Payment = InvoiceEditMapper.ResolvePayment(payload.Payment, PaymentOptions, Payment);
            BillDate = new DateTimeOffset(payload.BillDate.ToDateTime(TimeOnly.MinValue), DateTimeOffset.Now.Offset);
            Narration = payload.Notes ?? string.Empty;
            Discount = payload.Totals.DiscountTotal;
            DiscountEnabled = payload.Totals.DiscountTotal > 0m;
            BankCharges = payload.Totals.BankCharges;
            BankChargesEnabled = payload.Totals.BankCharges > 0m;
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
        => KaratMasters.Count == 0 ? null : KaratMasters;

    /// <summary>
    /// Why the bill cannot be priced as it stands: a line's karat is not one of the saved karats.
    /// Null when every karat is known. Print estimate asks this too, so that an estimate with a
    /// line priced with no gold in it does not reach a customer.
    /// </summary>
    public string? UnknownKaratProblem()
        => Lines.FirstOrDefault(line => !line.IsEmpty && line.KaratUnknown) is { } line
            ? $"Line {line.RowNumber}: {line.UnknownKaratMessage}"
            : null;

    /// <summary>
    /// The lines whose item is not one of the saved items. Such a line is saved with the name as
    /// typed, at the wastage and labour it shows. That is allowed (a one-off piece; on a gold line
    /// Tally's stock item, and the name printed, come from the karat), but it is also what a name cut
    /// short when Save was pressed looks like ("Rin" for "Ring"), so Save asks first. Empty while no
    /// items are saved at all: then there is nothing to pick from.
    /// </summary>
    private List<BillLineViewModel> ItemsNotSaved()
    {
        var saved = ItemMasters
            .Select(master => master.Name?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return saved.Count == 0
            ? []
            : Lines.Where(line => !line.IsEmpty && !saved.Contains(line.ItemName.Trim())).ToList();
    }

    private string? ItemsNotSavedSentence() => ItemsNotSaved() switch
    {
        [] => null,
        [var one] => $"Not saved: “{one.ItemName.Trim()}” on line {one.RowNumber} is not one of the saved items.",
        var several => $"Not saved: {several.Count} lines have an item that is not one of the saved items.",
    };

    // What Save would send, as text, to tell whether the bill changed while a question was open.
    private string PayloadText() => System.Text.Json.JsonSerializer.Serialize(BuildPayload());

    private static ConfirmRequest ItemsAsTypedQuestion(IReadOnlyList<BillLineViewModel> asTyped)
    {
        // Every one is listed: the one cut short may be the last of several one-off pieces.
        var facts = asTyped
            .Select(line => new ConfirmFact($"Line {line.RowNumber}", line.ItemName.Trim()))
            .ToList();
        var one = asTyped.Count == 1;
        return new ConfirmRequest(
            one ? $"Save “{asTyped[0].ItemName.Trim()}” as typed?" : $"Save {asTyped.Count} items as typed?",
            one
                ? $"“{asTyped[0].ItemName.Trim()}” on line {asTyped[0].RowNumber} is not one of the saved items. The bill keeps it as typed, at the price the line shows."
                : "These items are not saved items. The bill keeps each as typed, at the price its line shows.",
            "Save as typed",
            IsWarning: true,
            Note: "If a name was cut short, go back and pick the item from the list.",
            Facts: one ? null : facts);
    }

    private string? ValidateForSave()
    {
        var error = InvoicePayloadMapper.ValidateForSave(Lines, Rate24Kt, out var rateMissing);
        RateMissing = rateMissing;
        return error ?? UnknownKaratProblem();
    }
}
