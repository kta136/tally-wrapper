using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

public partial class BillsViewModel : ObservableObject, IBillsActionWorkflowHost
{
    private const int PageSize = 50;
    private const string SortWorkflow = "workflow";

    private readonly IBillsApiClient? _billsApi;
    private readonly AdminTokenStore? _adminTokens;
    private readonly BillsAdminTokenGate _adminTokenGate;
    private int _loadGeneration;

    // True while many rows are being ticked at once, so the list is not
    // re-evaluated for every row; the caller refreshes once at the end.
    private bool _selectingInBulk;

    /// <summary>
    /// Handler the shell wires up to open the admin-unlock dialog and wait until it closes.
    /// Used before admin-gated commands (change-number, mark-posted/pending, delete).
    /// </summary>
    public Func<CancellationToken, Task>? AdminUnlockHandler
    {
        get => _adminTokenGate.AdminUnlockHandler;
        set => _adminTokenGate.AdminUnlockHandler = value;
    }

    /// <summary>
    /// Handler the shell wires up to load a bill into the Invoice tab for editing
    /// and switch the active tab. Context-menu Edit invokes this.
    /// </summary>
    public Func<Guid, CancellationToken, Task>? EditBillHandler { get; set; }

    /// <summary>
    /// Handler the shell wires up to open the Change Number dialog and await its result.
    /// Returns (confirmed, newNumber, reason) — confirmed=false means user cancelled.
    /// </summary>
    public Func<BillListRowViewModel, CancellationToken, Task<(bool Confirmed, string NewNumber, string? Reason)>>? ChangeNumberPromptHandler { get; set; }

    /// <summary>
    /// Handler the shell wires up to prompt for a reason. Returns null if user cancels;
    /// otherwise the entered reason (min 4 chars guaranteed by dialog).
    /// </summary>
    public Func<string, string, CancellationToken, Task<string?>>? ReasonPromptHandler { get; set; }

    /// <summary>
    /// Handler the shell wires up to ask a yes/no question in the confirm
    /// overlay (repost, delete, push all).
    /// </summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

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

    public BillsViewModel() : this(null, null) { }

    public BillsViewModel(IBillsApiClient? billsApi, AdminTokenStore? adminTokens = null)
    {
        _billsApi = billsApi;
        _adminTokens = adminTokens;
        _adminTokenGate = new BillsAdminTokenGate(_adminTokens);
        var runner = new BillsActionRunner(this);
        var batch = new BillsBatchActionWorkflow(_billsApi, this, runner);
        var rows = new BillsRowActionWorkflow(_billsApi, _adminTokenGate, this, runner);
        var admin = new BillsAdminActionWorkflow(_billsApi, _adminTokenGate, this, runner);

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

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => CanAct);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => HasNext && !IsLoading && !IsActing);
        PrevPageCommand = new AsyncRelayCommand(PrevPageAsync, () => Skip > 0 && !IsLoading && !IsActing);
        ApplyFiltersCommand = new AsyncRelayCommand(ApplyFiltersAsync, () => CanAct);
        ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        ShowPostedDaysCommand = new RelayCommand(() => HideFullyPostedDays = false, () => HideFullyPostedDays);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelection);

        _pushSelected = batch.PushSelectedAsync;
        _retrySelected = batch.RetrySelectedAsync;
        _repostSelected = batch.RepostSelectedAsync;
        PushSelectedCommand = new AsyncRelayCommand(_pushSelected, () => CanActOnSelection(x => x.IsPendingLike, needsTally: true));
        PushAllPendingCommand = new AsyncRelayCommand(batch.PushAllPendingAsync, () => CanAct && IsTallyPushAllowed);
        RetrySelectedCommand = new AsyncRelayCommand(_retrySelected, () => CanActOnSelection(x => x.IsRetryable, needsTally: true));
        RepostSelectedCommand = new AsyncRelayCommand(_repostSelected, () => CanActOnSelection(x => x.CanBeReposted, needsTally: true));
        TallyPushSelectedCommand = new AsyncRelayCommand(
            TallyPushSelectedAsync,
            () => CanAct && IsTallyPushAllowed && GetTallyPushMode() != TallyPushMode.None);

        PushRowCommand = RowCommand(rows.PushRowAsync, x => x.CanBePushed, needsTally: true);
        RetryRowCommand = RowCommand(rows.RetryRowAsync, x => x.CanBeRetried, needsTally: true);
        RepostRowCommand = RowCommand(rows.RepostRowAsync, x => x.CanBeReposted, needsTally: true);
        EditRowCommand = RowCommand(rows.EditRowAsync, x => x.CanBeEdited);
        CopyInvoiceNumberCommand = new RelayCommand<BillListRowViewModel?>(rows.CopyInvoiceNumber, row => row?.CanCopyInvoiceNumber == true);

        ChangeNumberRowCommand = RowCommand(admin.ChangeNumberRowAsync, x => x.CanChangeNumber);
        MarkPostedRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(admin.MarkPostedRowAsync, row => CanActOnContextRows(row, x => x.CanMarkPosted));
        MarkPostedSelectedCommand = new AsyncRelayCommand(admin.MarkPostedSelectedAsync, () => CanActOnSelection(x => x.CanMarkPosted));
        MarkPendingRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(admin.MarkPendingRowAsync, row => CanActOnContextRows(row, x => x.CanMarkPending));
        MarkPendingSelectedCommand = new AsyncRelayCommand(admin.MarkPendingSelectedAsync, () => CanActOnSelection(x => x.CanMarkPending));
        DeleteRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(admin.DeleteRowAsync, row => CanActOnContextRows(row, x => x.CanBeDeleted));
        DeleteSelectedCommand = new AsyncRelayCommand(admin.DeleteSelectedAsync, () => CanActOnSelection(x => x.CanBeDeleted));
    }

    // The combined "Push to Tally" button picks one of these three by what is selected.
    private readonly Func<CancellationToken, Task> _pushSelected;
    private readonly Func<CancellationToken, Task> _retrySelected;
    private readonly Func<CancellationToken, Task> _repostSelected;

    public ObservableCollection<BillListRowViewModel> Items { get; }

    /// <summary>
    /// View over <see cref="Items"/> grouped by <see cref="BillListRowViewModel.BillDate"/>.
    /// The list view in BillsView binds to this so date headers appear between rows.
    /// </summary>
    public ListCollectionView GroupedItems { get; }

    public IReadOnlyList<string> StateFilterOptions { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand NextPageCommand { get; }
    public IAsyncRelayCommand PrevPageCommand { get; }
    public IAsyncRelayCommand ApplyFiltersCommand { get; }

    public IAsyncRelayCommand ClearFiltersCommand { get; }
    public IRelayCommand ShowPostedDaysCommand { get; }
    public IAsyncRelayCommand PushSelectedCommand { get; }
    public IAsyncRelayCommand PushAllPendingCommand { get; }

    public bool IsAdminUnlocked => _adminTokens?.IsUnlocked == true;
    public IAsyncRelayCommand RetrySelectedCommand { get; }
    public IAsyncRelayCommand RepostSelectedCommand { get; }
    public IAsyncRelayCommand TallyPushSelectedCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }

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
    [ObservableProperty] private bool isTallyPushAllowed;
    [ObservableProperty] private string tallyPushBlockReason = "Tally connection has not been checked yet.";
    [ObservableProperty] private string statusMessage = string.Empty;
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

    public bool AreAllVisibleSelected
    {
        get => Items.Count > 0 && Items.All(x => x.IsSelected);
        set => ToggleVisibleSelection(value);
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
                StatusMessage = "Checking Tally connection…";
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

        StatusMessage = TallyPushBlockReason;
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
        if (_billsApi is null)
        {
            return;
        }

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
        PushSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        RepostSelectedCommand.NotifyCanExecuteChanged();
        TallyPushSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TallyPushButtonLabel));
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

    private bool CanAct => _billsApi is not null && !IsLoading && !IsActing;

    private bool CanActOnSelection(Func<BillListRowViewModel, bool> allowed, bool needsTally = false)
        => CanAct && HasSelection && (!needsTally || IsTallyPushAllowed) && SelectedRows.All(allowed);

    /// <summary>
    /// A context-menu action applies to the whole selection when the clicked
    /// row is part of a multi-row selection, otherwise to that row alone.
    /// </summary>
    private bool CanActOnContextRows(BillListRowViewModel? row, Func<BillListRowViewModel, bool> allowed)
    {
        if (!CanAct || row is null)
        {
            return false;
        }

        return row.IsSelected && SelectedCount > 1 ? SelectedRows.All(allowed) : allowed(row);
    }

    private AsyncRelayCommand<BillListRowViewModel?> RowCommand(
        Func<BillListRowViewModel?, CancellationToken, Task> action,
        Func<BillListRowViewModel, bool> allowed,
        bool needsTally = false)
        => new(action, row => row is not null && allowed(row) && !IsLoading && !IsActing && (!needsTally || IsTallyPushAllowed));

    public enum TallyPushMode { None, Push, Retry, Repost }

    private TallyPushMode GetTallyPushMode()
    {
        if (!HasSelection) return TallyPushMode.None;
        var selected = SelectedRows.ToArray();
        if (selected.All(x => x.IsPendingLike)) return TallyPushMode.Push;
        if (selected.All(x => x.IsRetryable)) return TallyPushMode.Retry;
        if (selected.All(x => BillStateCapabilities.IsPosted(x.State))) return TallyPushMode.Repost;
        return TallyPushMode.None;
    }

    public string TallyPushButtonLabel => GetTallyPushMode() switch
    {
        TallyPushMode.Push => "Push to Tally",
        TallyPushMode.Retry => "Retry",
        TallyPushMode.Repost => "Repost…",
        _ => "Push to Tally",
    };

    private Task TallyPushSelectedAsync(CancellationToken cancellationToken) => GetTallyPushMode() switch
    {
        TallyPushMode.Push => _pushSelected(cancellationToken),
        TallyPushMode.Retry => _retrySelected(cancellationToken),
        TallyPushMode.Repost => _repostSelected(cancellationToken),
        _ => Task.CompletedTask,
    };

    private Task NextPageAsync(CancellationToken cancellationToken)
    {
        if (!HasNext)
        {
            return Task.CompletedTask;
        }

        Skip += PageSize;
        return LoadAsync(cancellationToken);
    }

    private Task PrevPageAsync(CancellationToken cancellationToken)
    {
        Skip = Math.Max(0, Skip - PageSize);
        return LoadAsync(cancellationToken);
    }

    // A changed filter starts again from the first page; otherwise the old
    // offset gives empty "Showing 101-100" pages.
    private Task ApplyFiltersAsync(CancellationToken cancellationToken)
    {
        Skip = 0;
        return LoadAsync(cancellationToken);
    }

    private Task ClearFiltersAsync(CancellationToken cancellationToken)
    {
        StateFilter = "All";
        FromDate = null;
        ToDate = null;
        SearchQuery = string.Empty;
        return ApplyFiltersAsync(cancellationToken);
    }
}
