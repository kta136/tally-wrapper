using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

public partial class BillsViewModel : ObservableObject
{
    private const int PageSize = 50;
    private const string SortWorkflow = "workflow";

    private readonly IBillsApiClient _billsApi;
    private readonly AdminTokenStore? _adminTokens;
    private readonly BillsAdminTokenGate _adminTokenGate;
    private readonly BillActions _actions;
    private int _loadGeneration;

    // True while many rows are being ticked at once, so the list is not
    // re-evaluated for every row; the caller refreshes once at the end.
    private bool _selectingInBulk;

    /// <summary>
    /// Handler the shell wires up to load a bill into the Invoice tab for editing
    /// and switch the active tab. Context-menu Edit invokes this.
    /// </summary>
    public Func<Guid, CancellationToken, Task>? EditBillHandler { get; set; }

    /// <summary>
    /// Opens Bill Details for the focused bill. The shell owns the dialog and
    /// hands its command over, so the row context menu can bind it like every
    /// other item.
    /// </summary>
    public System.Windows.Input.ICommand? OpenDetailsCommand { get; set; }

    /// <summary>Opens the print preview for the selected bills; owned by the shell, as above.</summary>
    public System.Windows.Input.ICommand? PrintSelectedCommand { get; set; }

    /// <summary>
    /// Handler the shell wires up to force a Tally-company health check before
    /// push/retry/repost commands call the bills API.
    /// </summary>
    public Func<CancellationToken, Task<SystemHealthSnapshot?>>? RefreshTallyHealthHandler { get; set; }

    /// <param name="settings">Where Change bill number reads the prefix, suffix and padding a number is formatted with.</param>
    public BillsViewModel(
        IBillsApiClient billsApi,
        IDialogService dialogs,
        AdminTokenStore? adminTokens = null,
        SettingsViewModel? settings = null)
    {
        _billsApi = billsApi;
        _adminTokens = adminTokens;
        _adminTokenGate = new BillsAdminTokenGate(_adminTokens, dialogs);
        _actions = new BillActions(
            _billsApi, dialogs, _adminTokens, EnsureTallyPushAllowedAsync, () => settings?.Settings?.Numbering);

        // Re-publish admin state to XAML so admin-only actions
        // (Change Bill Number, Mark Posted/Pending, Delete, Delete Selected)
        // can hide themselves when locked.
        if (_adminTokens is not null)
        {
            _adminTokens.Changed += _ => OnPropertyChanged(nameof(IsAdminUnlocked));
        }

        Items = new ObservableCollection<BillListRowViewModel>();
        GroupedItems = new ListCollectionView(Items)
        {
            IsLiveGrouping = true,
            IsLiveSorting = true,
            Filter = FilterRow,
        };
        // Primary: BillDate desc — most recent date group on top.
        // Secondary: InvoiceNumberSortKey desc — within the same day, highest
        // invoice number first. Parsing the trailing core (not the formatted
        // string) keeps natural order across legacy mixed formats and avoids
        // the "renamed bill bubbles up by CreatedAt" surprise.
        // Tertiary: CreatedAtUtc desc — tiebreak for bills without a number
        // (pure drafts) or any rare duplicate sort key.
        GroupedItems.SortDescriptions.Add(new SortDescription(nameof(BillListRowViewModel.BillDate), ListSortDirection.Descending));
        GroupedItems.SortDescriptions.Add(new SortDescription(nameof(BillListRowViewModel.InvoiceNumberSortKey), ListSortDirection.Descending));
        GroupedItems.SortDescriptions.Add(new SortDescription(nameof(BillListRowViewModel.CreatedAtUtc), ListSortDirection.Descending));
        GroupedItems.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BillListRowViewModel.BillDate)));
        GroupedItems.LiveGroupingProperties.Add(nameof(BillListRowViewModel.BillDate));
        GroupedItems.LiveSortingProperties.Add(nameof(BillListRowViewModel.BillDate));
        GroupedItems.LiveSortingProperties.Add(nameof(BillListRowViewModel.InvoiceNumberSortKey));
        GroupedItems.LiveSortingProperties.Add(nameof(BillListRowViewModel.CreatedAtUtc));
        StateFilterOptions =
        [
            "All",
            BillStates.Pending,
            BillStates.Draft,
            BillStates.Posting,
            BillStates.Posted,
            BillStates.Failed,
            BillStates.ReconciliationRequired,
            BillStates.Revised,
            BillStates.Voided,
        ];

        // Each of these is one BillActions call on the rows it applies to, run through RunAsync.
        // The commands this class implements itself are the [RelayCommand] methods further down.
        PushSelectedCommand = SelectionCommand(
            (rows, busy, ct) => _actions.PushSelectedAsync(rows.Select(row => row.Id).ToArray(), busy, ct), x => x.IsPendingLike, needsTally: true);
        PushAllPendingCommand = new AsyncRelayCommand(
            ct => RunAsync(busy => _actions.PushAllPendingAsync(busy, ct), ct), () => CanAct && IsTallyPushAllowed);
        RetrySelectedCommand = SelectionCommand(
            (rows, busy, ct) => _actions.RetryAsync(Refs(rows), "Retry selected from Bills tab", busy, ct), x => x.IsRetryable, needsTally: true);
        RepostSelectedCommand = SelectionCommand(
            (rows, busy, ct) => _actions.RepostAsync(Refs(rows), "Batch repost from Bills tab", busy, ct), x => x.CanBeReposted, needsTally: true);

        PushRowCommand = RowCommand(
            (row, busy, ct) => _actions.PushAsync(BillRef.Of(row), "Context menu push", busy, ct), x => x.CanBePushed, needsTally: true);
        RetryRowCommand = RowCommand(
            (row, busy, ct) => _actions.RetryAsync([BillRef.Of(row)], "Context menu retry", busy, ct), x => x.CanBeRetried, needsTally: true);
        RepostRowCommand = RowCommand(
            (row, busy, ct) => _actions.RepostAsync([BillRef.Of(row)], "Context menu repost", busy, ct), x => x.CanBeReposted, needsTally: true);
        EditRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(EditRowAsync, row => row is not null && row.CanBeEdited && CanAct);
        CopyInvoiceNumberCommand = new RelayCommand<BillListRowViewModel?>(CopyInvoiceNumber, row => row?.CanCopyInvoiceNumber == true);

        ChangeNumberRowCommand = RowCommand(_actions.ChangeNumberAsync, x => x.CanChangeNumber);
        MarkPostedRowCommand = ContextRowsCommand(_actions.MarkPushedAsync, x => x.CanMarkPosted);
        MarkPostedSelectedCommand = SelectionCommand(_actions.MarkPushedAsync, x => x.CanMarkPosted);
        MarkPendingRowCommand = ContextRowsCommand(_actions.MarkPendingAsync, x => x.CanMarkPending);
        MarkPendingSelectedCommand = SelectionCommand(_actions.MarkPendingAsync, x => x.CanMarkPending);
        // One bill is deleted after a dry run that says what Tally holds; several go in one call.
        DeleteRowCommand = ContextRowsCommand(
            (rows, busy, ct) => rows.Count == 1 ? _actions.DeleteAsync(rows[0], busy, ct) : _actions.DeleteSelectedAsync(rows, busy, ct),
            x => x.CanBeDeleted);
        DeleteSelectedCommand = SelectionCommand(_actions.DeleteSelectedAsync, x => x.CanBeDeleted);
    }

    private static BillRef[] Refs(IReadOnlyList<BillListRowViewModel> rows) => rows.Select(BillRef.Of).ToArray();

    public ObservableCollection<BillListRowViewModel> Items { get; }

    /// <summary>
    /// View over <see cref="Items"/> grouped by <see cref="BillListRowViewModel.BillDate"/>.
    /// The list view in BillsView binds to this so date headers appear between rows.
    /// </summary>
    public ListCollectionView GroupedItems { get; }

    public IReadOnlyList<string> StateFilterOptions { get; }

    public IAsyncRelayCommand PushSelectedCommand { get; }
    public IAsyncRelayCommand PushAllPendingCommand { get; }

    public bool IsAdminUnlocked => _adminTokens?.IsUnlocked == true;
    public IAsyncRelayCommand RetrySelectedCommand { get; }
    public IAsyncRelayCommand RepostSelectedCommand { get; }

    public IAsyncRelayCommand<BillListRowViewModel?> PushRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> RetryRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> RepostRowCommand { get; }
    public IRelayCommand<BillListRowViewModel?> CopyInvoiceNumberCommand { get; }

    public IAsyncRelayCommand<BillListRowViewModel?> EditRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> ChangeNumberRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> MarkPostedRowCommand { get; }
    public IAsyncRelayCommand MarkPostedSelectedCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> MarkPendingRowCommand { get; }
    public IAsyncRelayCommand MarkPendingSelectedCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> DeleteRowCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }

    [ObservableProperty] private string stateFilter = "All";
    [ObservableProperty] private DateTime? fromDate;
    [ObservableProperty] private DateTime? toDate;
    [ObservableProperty] private string searchQuery = string.Empty;
    [ObservableProperty] private int skip;
    [ObservableProperty] private int total;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isActing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterNotice), nameof(FooterNoticeTone), nameof(HasFooterNotice))]
    private bool isTallyPushAllowed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterNotice), nameof(FooterNoticeTone), nameof(HasFooterNotice))]
    private string tallyPushBlockReason = "Tally connection has not been checked yet.";

    [ObservableProperty] private string statusMessage = string.Empty;

    /// <summary>What the last action said, with its tone; see <see cref="Say"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterNotice), nameof(FooterNoticeTone), nameof(HasFooterNotice))]
    private string notice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterNoticeTone))]
    private string noticeTone = BillStateDisplay.ToneNeutral;

    /// <summary>
    /// The status line at the foot of the tab: what the last action said, or, while nothing has
    /// been said, why the push buttons are greyed out when Tally cannot be reached.
    /// </summary>
    public string FooterNotice => Notice.Length > 0 ? Notice : IsTallyPushAllowed ? string.Empty : TallyPushBlockReason;

    public string FooterNoticeTone => Notice.Length > 0 ? NoticeTone : BillStateDisplay.ToneWarn;

    public bool HasFooterNotice => FooterNotice.Length > 0;
    [ObservableProperty] private BillSummaryItem? selectedBill;

    /// <summary>
    /// When true, date groups in which every bill is already <c>posted</c> are dropped from
    /// the list — leaves only days that still have unposted (pending/draft/posting/failed) work.
    /// </summary>
    [ObservableProperty] private bool hideFullyPostedDays = true;

    /// <summary>
    /// Set of bill dates whose every visible row is in the <c>posted</c> state. Recomputed on
    /// every <see cref="ResetRows"/> and consulted by the <see cref="GroupedItems"/> filter
    /// when <see cref="HideFullyPostedDays"/> is on.
    /// </summary>
    private readonly HashSet<DateOnly?> _fullyPostedDates = [];

    public bool HasNext => Skip + PageSize < Total;

    // The list is loaded without a count, so until the last page the server
    // only knows there is at least one more bill.
    private bool _hasMorePages;

    /// <summary>"120", or "50+" while more pages follow.</summary>
    public string TotalDisplay => _hasMorePages ? $"{Skip + Items.Count}+" : Total.ToString();
    public int VisibleCount => GroupedItems.Count;
    public int Showing => VisibleCount;
    public int HiddenByPostedDaysCount => Math.Max(0, Items.Count - VisibleCount);
    public bool IsFilteredEmpty => Items.Count > 0 && VisibleCount == 0;
    public int SelectedCount => Items.Count(x => x.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public bool CanPrintSelected => HasSelection;
    public bool IsSelectionBarVisible => HasSelection;
    public string SelectionSummaryText => HasSelection ? $"{SelectedCount} selected" : "No selection";

    /// <summary>True when every row is ticked, null when only some are (the header box shows a dash), false when none is.</summary>
    public bool? AreAllVisibleSelected
    {
        get
        {
            if (Items.Count == 0 || !Items.Any(x => x.IsSelected)) return false;
            return Items.All(x => x.IsSelected) ? true : null;
        }
        set => ToggleVisibleSelection(value == true);
    }

    /// <summary>Bills waiting for the operator to push them (pending or draft).</summary>
    public int WaitingForTallyCount => Items.Count(x => x.IsPendingLike);

    /// <summary>Bills with a push in flight.</summary>
    public int PostingCount => Items.Count(x => x.State == BillStates.Posting);

    /// <summary>Bills that failed or need their voucher checked in Tally before anything else is done to them.</summary>
    public int NeedAttentionCount => Items.Count(x => x.State is BillStates.Failed or BillStates.ReconciliationRequired);

    public bool HasWaitingForTally => WaitingForTallyCount > 0;
    public bool HasPosting => PostingCount > 0;
    public bool HasNeedAttention => NeedAttentionCount > 0;

    public string WaitingForTallyText => $"{WaitingForTallyCount} waiting for Tally";
    public string PostingText => $"{PostingCount} posting";
    public string NeedAttentionText => $"{NeedAttentionCount} need attention";

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(WaitingForTallyCount));
        OnPropertyChanged(nameof(PostingCount));
        OnPropertyChanged(nameof(NeedAttentionCount));
        OnPropertyChanged(nameof(HasWaitingForTally));
        OnPropertyChanged(nameof(HasPosting));
        OnPropertyChanged(nameof(HasNeedAttention));
        OnPropertyChanged(nameof(WaitingForTallyText));
        OnPropertyChanged(nameof(PostingText));
        OnPropertyChanged(nameof(NeedAttentionText));
    }

    public IReadOnlyList<Guid> SelectedBillIds =>
        SelectedRows.Select(x => x.Id).ToArray();

    private IEnumerable<BillListRowViewModel> SelectedRows => Items.Where(x => x.IsSelected);

    public BillListRowViewModel? SelectedRow =>
        Items.FirstOrDefault(x => x.IsSelected)
        ?? (SelectedBill is { } s ? Items.FirstOrDefault(x => x.Id == s.Id) : null);

    partial void OnSkipChanged(int value) => RefreshComputed();
    partial void OnTotalChanged(int value) => RefreshComputed();
    partial void OnIsLoadingChanged(bool value) => RefreshComputed();
    partial void OnIsActingChanged(bool value) => RefreshComputed();
    partial void OnIsTallyPushAllowedChanged(bool value) => RefreshComputed();
    partial void OnStateFilterChanged(string value) => RefreshComputed();
    partial void OnSearchQueryChanged(string value) => RefreshComputed();

    public void ApplyTallyHealthSnapshot(SystemHealthSnapshot? snapshot)
    {
        var health = snapshot?.TallyCompany;
        if (snapshot is null || !snapshot.ApiReachable)
        {
            IsTallyPushAllowed = false;
            TallyPushBlockReason = "Tally push blocked: cloud/API is unavailable.";
            return;
        }

        if (health is null)
        {
            IsTallyPushAllowed = false;
            TallyPushBlockReason = "Tally push blocked: Tally connection has not been checked.";
            return;
        }

        var ready = string.Equals(health.Status, TallyHealthStatuses.Healthy, StringComparison.OrdinalIgnoreCase)
            && health.TallyReachable
            && health.ActiveCompanyOpen;

        IsTallyPushAllowed = ready;
        TallyPushBlockReason = ready
            ? string.Empty
            : $"Tally push blocked: {health.Message}";
    }

    public async Task<bool> EnsureTallyPushAllowedAsync(CancellationToken cancellationToken)
    {
        if (RefreshTallyHealthHandler is not null)
        {
            try
            {
                Say("Checking Tally connection…");
                ApplyTallyHealthSnapshot(await RefreshTallyHealthHandler(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                IsTallyPushAllowed = false;
                TallyPushBlockReason = $"Tally push blocked: Tally connection check failed. {ex.Message}";
            }
        }

        if (IsTallyPushAllowed)
        {
            return true;
        }

        Say(TallyPushBlockReason, BillStateDisplay.ToneWarn);
        return false;
    }

    public void SelectOnly(BillListRowViewModel? row)
    {
        if (row is null)
        {
            ClearSelection();
            return;
        }

        SelectInBulk(item => ReferenceEquals(item, row));
        SelectedBill = row.Item;
        RefreshSelectionState();
    }

    public void EnsureContextSelection(BillListRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.IsSelected || SelectedCount <= 1)
        {
            SelectOnly(row);
            return;
        }

        SelectedBill = row.Item;
        RefreshSelectionState();
    }

    public void ToggleVisibleSelection(bool isSelected)
    {
        SelectInBulk(_ => isSelected);
        if (!isSelected)
        {
            SelectedBill = null;
        }
        else if (SelectedBill is null && Items.Count > 0)
        {
            SelectedBill = Items[0].Item;
        }

        RefreshSelectionState();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    public void ClearSelection()
    {
        SelectInBulk(_ => false);
        SelectedBill = null;
        RefreshSelectionState();
    }

    private void SelectInBulk(Func<BillListRowViewModel, bool> isSelected)
    {
        _selectingInBulk = true;
        try
        {
            foreach (var item in Items)
            {
                item.IsSelected = isSelected(item);
            }
        }
        finally
        {
            _selectingInBulk = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // Only the newest load may write the list: an older, slower response
        // (previous page or filter) must not overwrite a newer one.
        var generation = ++_loadGeneration;
        IsLoading = true;
        StatusMessage = "Loading bills…";
        try
        {
            var state = StateFilter == "All" ? null : StateFilter;
            var from = FromDate is null ? (DateOnly?)null : DateOnly.FromDateTime(FromDate.Value);
            var to = ToDate is null ? (DateOnly?)null : DateOnly.FromDateTime(ToDate.Value);

            var priorSelectedIds = SelectedBillIds;
            var priorFocusedId = SelectedBill?.Id;

            var list = await _billsApi.SearchAsync(
                new BillSearchFilter(state, from, to, Skip, PageSize, SortWorkflow, IncludeTotal: false, SearchQuery),
                cancellationToken);
            if (generation != _loadGeneration)
            {
                return;
            }

            ResetRows(list.Items);
            _hasMorePages = list.HasMore == true;
            Total = list.Total;
            LoadError = null;

            _selectingInBulk = true;
            try
            {
                SelectedBill = BillsSelectionRestorer.Restore(Items, priorSelectedIds, priorFocusedId);
            }
            finally
            {
                _selectingInBulk = false;
            }

            RefreshSelectionState();

            StatusMessage = list.Total == 0
                ? "No bills found."
                : list.HasMore == true
                    ? $"Showing {Skip + 1}–{Skip + Items.Count}"
                    : $"Showing {Skip + 1}–{Skip + Items.Count} of {list.Total}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
            if (generation == _loadGeneration)
            {
                LoadError = ApiResponseReader.FormatError(ex);
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>
    /// Why the last load failed, or null. The view shows it above the list:
    /// an empty list by itself looks exactly like "there are no bills".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? loadError;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    private void ResetRows(IReadOnlyList<BillSummaryItem> rows)
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnRowPropertyChanged;
        }

        Items.Clear();
        foreach (var row in rows)
        {
            var item = new BillListRowViewModel(row);
            item.PropertyChanged += OnRowPropertyChanged;
            Items.Add(item);
        }

        RecomputeFullyPostedDates();
        GroupedItems.Refresh();
        RaiseCounts();
    }

    private bool FilterRow(object obj)
    {
        if (!HideFullyPostedDays)
        {
            return true;
        }

        return obj is BillListRowViewModel row && !_fullyPostedDates.Contains(row.BillDate);
    }

    private void RecomputeFullyPostedDates()
    {
        _fullyPostedDates.Clear();
        foreach (var group in Items.GroupBy(x => x.BillDate))
        {
            if (group.All(x => x.State == BillStates.Posted))
            {
                _fullyPostedDates.Add(group.Key);
            }
        }
    }

    partial void OnHideFullyPostedDaysChanged(bool value)
    {
        GroupedItems.Refresh();
        RefreshComputed();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selectingInBulk
            || e.PropertyName != nameof(BillListRowViewModel.IsSelected)
            || sender is not BillListRowViewModel row)
        {
            return;
        }

        // The focused bill follows the tick; unticking it hands focus to another ticked row.
        if (row.IsSelected)
        {
            SelectedBill = row.Item;
        }
        else if (SelectedBill?.Id == row.Id)
        {
            SelectedBill = SelectedRows.FirstOrDefault()?.Item;
        }

        RefreshSelectionState();
    }

    private void RefreshComputed()
    {
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(TotalDisplay));
        OnPropertyChanged(nameof(Showing));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HiddenByPostedDaysCount));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        RefreshSelectionState();
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyFiltersCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        PushAllPendingCommand.NotifyCanExecuteChanged();
        ShowPostedDaysCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanPrintSelected));
        OnPropertyChanged(nameof(IsSelectionBarVisible));
        OnPropertyChanged(nameof(AreAllVisibleSelected));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(SelectedBillIds));
        // The toolbar's Edit and Change bill number, and Ctrl+Shift+E, take this row as their
        // parameter. It was never announced, so they were handed nothing and stayed disabled.
        OnPropertyChanged(nameof(SelectedRow));
        PushSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        RepostSelectedCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        PushRowCommand.NotifyCanExecuteChanged();
        RetryRowCommand.NotifyCanExecuteChanged();
        RepostRowCommand.NotifyCanExecuteChanged();
        EditRowCommand.NotifyCanExecuteChanged();
        ChangeNumberRowCommand.NotifyCanExecuteChanged();
        MarkPostedRowCommand.NotifyCanExecuteChanged();
        MarkPostedSelectedCommand.NotifyCanExecuteChanged();
        MarkPendingRowCommand.NotifyCanExecuteChanged();
        MarkPendingSelectedCommand.NotifyCanExecuteChanged();
        DeleteRowCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct => !IsLoading && !IsActing;

    private bool CanActOnSelection(Func<BillListRowViewModel, bool> allowed, bool needsTally = false)
        => CanAct && HasSelection && (!needsTally || IsTallyPushAllowed) && SelectedRows.All(allowed);

    /// <summary>
    /// The rows a context-menu action applies to: the whole selection when the clicked
    /// row is part of a multi-row selection, otherwise that row alone.
    /// </summary>
    private IReadOnlyList<BillListRowViewModel> ContextRows(BillListRowViewModel? row)
    {
        if (row is null)
        {
            return [];
        }

        return row.IsSelected && SelectedCount > 1 ? SelectedRows.ToArray() : [row];
    }

    /// <summary>
    /// One action as this screen shows it: busy with the action's own text from the moment it
    /// starts sending, then what it came to, then the list again if anything was sent. An action
    /// that comes to nothing (the operator said no) leaves the screen as it is.
    /// </summary>
    private async Task RunAsync(Func<Action<string>, Task<BillActionResult?>> action, CancellationToken cancellationToken)
    {
        BillActionResult? result;
        try
        {
            result = await action(status =>
            {
                IsActing = true;
                Say(status);
            });
        }
        finally
        {
            IsActing = false;
        }

        if (result is null)
        {
            return;
        }

        Say(result.Message, result.Tone);
        if (result.Sent)
        {
            await LoadAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Says something on the status line at the foot of the tab, where it stays until the next
    /// thing is said. <see cref="StatusMessage"/> is not that line: a load writes its own
    /// summary there straight after an action, and nothing on screen shows it.
    /// </summary>
    private void Say(string message, string tone = BillStateDisplay.ToneNeutral)
    {
        StatusMessage = message;
        Notice = message;
        NoticeTone = tone;
    }

    /// <summary>For the shell, when something it does for this tab goes wrong (opening the print preview).</summary>
    public void ReportProblem(string message) => Say(message, BillStateDisplay.ToneErr);

    /// <summary>A toolbar command: the action on every ticked row, offered while each of them allows it.</summary>
    private AsyncRelayCommand SelectionCommand(
        Func<IReadOnlyList<BillListRowViewModel>, Action<string>, CancellationToken, Task<BillActionResult?>> action,
        Func<BillListRowViewModel, bool> allowed,
        bool needsTally = false)
        => new(
            ct => RunAsync(busy => action(SelectedRows.ToArray(), busy, ct), ct),
            () => CanActOnSelection(allowed, needsTally));

    /// <summary>A row-menu command that takes the whole selection when the clicked row is part of one.</summary>
    private AsyncRelayCommand<BillListRowViewModel?> ContextRowsCommand(
        Func<IReadOnlyList<BillListRowViewModel>, Action<string>, CancellationToken, Task<BillActionResult?>> action,
        Func<BillListRowViewModel, bool> allowed)
        => new(
            (row, ct) => row is null ? Task.CompletedTask : RunAsync(busy => action(ContextRows(row), busy, ct), ct),
            row => CanAct && row is not null && ContextRows(row).All(allowed));

    /// <summary>A row-menu command on the clicked row alone.</summary>
    private AsyncRelayCommand<BillListRowViewModel?> RowCommand(
        Func<BillListRowViewModel, Action<string>, CancellationToken, Task<BillActionResult?>> action,
        Func<BillListRowViewModel, bool> allowed,
        bool needsTally = false)
        => new(
            (row, ct) => row is null ? Task.CompletedTask : RunAsync(busy => action(row, busy, ct), ct),
            row => row is not null && allowed(row) && CanAct && (!needsTally || IsTallyPushAllowed));

    private async Task EditRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null || EditBillHandler is null) return;

        // Saving an edit to a posted bill needs admin, so ask before the
        // operator has retyped anything rather than at Save.
        if (BillStateCapabilities.IsPosted(row.State))
        {
            var (token, _) = await _adminTokenGate.EnsureAsync(cancellationToken);
            if (token is null)
            {
                Say($"{row.InvoiceNumberDisplay} is posted; editing it needs admin unlock.", BillStateDisplay.ToneWarn);
                return;
            }
        }

        try
        {
            await EditBillHandler(row.Id, cancellationToken);
            Say($"Editing {row.InvoiceNumberDisplay} in the Invoice tab.");
        }
        catch (Exception ex)
        {
            Say($"Edit failed: {ApiResponseReader.FormatError(ex)}", BillStateDisplay.ToneErr);
        }
    }

    private void CopyInvoiceNumber(BillListRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.InvoiceNumber)) return;
        try
        {
            System.Windows.Clipboard.SetText(row.InvoiceNumber!);
            Say($"Copied invoice number: {row.InvoiceNumber}", BillStateDisplay.ToneOk);
        }
        catch (Exception ex)
        {
            Say($"Copy failed: {ex.Message}", BillStateDisplay.ToneErr);
        }
    }

    private bool CanGoToNextPage => HasNext && CanAct;

    private bool CanGoToPrevPage => Skip > 0 && CanAct;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(HideFullyPostedDays))]
    private void ShowPostedDays() => HideFullyPostedDays = false;

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private Task NextPageAsync(CancellationToken cancellationToken)
    {
        if (!HasNext)
        {
            return Task.CompletedTask;
        }

        Skip += PageSize;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanGoToPrevPage))]
    private Task PrevPageAsync(CancellationToken cancellationToken)
    {
        Skip = Math.Max(0, Skip - PageSize);
        return LoadAsync(cancellationToken);
    }

    // A changed filter starts again from the first page; otherwise the old
    // offset gives empty "Showing 101-100" pages.
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task ApplyFiltersAsync(CancellationToken cancellationToken)
    {
        Skip = 0;
        return LoadAsync(cancellationToken);
    }

    [RelayCommand]
    private Task ClearFiltersAsync(CancellationToken cancellationToken)
    {
        StateFilter = "All";
        FromDate = null;
        ToDate = null;
        SearchQuery = string.Empty;
        return ApplyFiltersAsync(cancellationToken);
    }
}
