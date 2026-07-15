using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

public partial class BillsViewModel : ObservableObject, IBillsActionWorkflowHost
{
    private const int PageSize = 50;
    private const string SortWorkflow = "workflow";

    private readonly IBillsApiClient? _billsApi;
    private readonly AdminTokenStore? _adminTokens;
    private readonly BillsAdminTokenGate _adminTokenGate;
    private readonly BillsActionWorkflow _actionWorkflow;
    private int _selectedCount;

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
        _actionWorkflow = new BillsActionWorkflow(_billsApi, _adminTokenGate, this);

        // Re-publish admin state to XAML so admin-only actions
        // (Change Bill Number, Mark Posted/Pending, Delete, Delete Selected)
        // can hide themselves when locked. Matches the pattern in
        // SettingsViewModel.IsAdminFeaturesVisible.
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

        RefreshCommand = new AsyncRelayCommand(LoadAsync, CanRefresh);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, CanNextPage);
        PrevPageCommand = new AsyncRelayCommand(PrevPageAsync, CanPrevPage);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        ShowPostedDaysCommand = new RelayCommand(() => HideFullyPostedDays = false, () => HideFullyPostedDays);
        PushSelectedCommand = new AsyncRelayCommand(PushSelectedAsync, CanPushSelected);
        PushAllPendingCommand = new AsyncRelayCommand(PushAllPendingAsync, CanPushAllPending);
        RetrySelectedCommand = new AsyncRelayCommand(RetrySelectedAsync, CanRetrySelected);
        VoidSelectedCommand = new AsyncRelayCommand(VoidSelectedAsync, CanVoidSelected);
        ReviseSelectedCommand = new AsyncRelayCommand(ReviseSelectedAsync, CanReviseSelected);
        RepostSelectedCommand = new AsyncRelayCommand(RepostSelectedAsync, CanRepostSelected);
        TallyPushSelectedCommand = new AsyncRelayCommand(TallyPushSelectedAsync, CanTallyPushSelected);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasSelection);

        PushRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(PushRowAsync, row => row?.CanBePushed == true && !IsLoading && !IsActing && IsTallyPushAllowed);
        RetryRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(RetryRowAsync, row => row?.CanBeRetried == true && !IsLoading && !IsActing && IsTallyPushAllowed);
        RepostRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(RepostRowAsync, row => row?.CanBeReposted == true && !IsLoading && !IsActing && IsTallyPushAllowed);
        ReviseRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(ReviseRowAsync, row => row?.CanBeRevised == true && !IsLoading && !IsActing);
        VoidRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(VoidRowAsync, row => row?.CanBeVoided == true && !IsLoading && !IsActing);
        CopyInvoiceNumberCommand = new RelayCommand<BillListRowViewModel?>(CopyInvoiceNumber, row => row?.CanCopyInvoiceNumber == true);

        EditRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(EditRowAsync, row => row?.CanBeEdited == true && !IsLoading && !IsActing);
        ChangeNumberRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(ChangeNumberRowAsync, row => row?.CanChangeNumber == true && !IsLoading && !IsActing);
        MarkPostedRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(MarkPostedRowAsync, CanMarkPostedFromContext);
        MarkPostedSelectedCommand = new AsyncRelayCommand(MarkPostedSelectedAsync, CanMarkPostedSelected);
        MarkPendingRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(MarkPendingRowAsync, CanMarkPendingFromContext);
        MarkPendingSelectedCommand = new AsyncRelayCommand(MarkPendingSelectedAsync, CanMarkPendingSelected);
        DeleteRowCommand = new AsyncRelayCommand<BillListRowViewModel?>(DeleteRowAsync, CanDeleteFromContext);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, CanDeleteSelected);
    }

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
    public IRelayCommand ClearFiltersCommand { get; }
    public IRelayCommand ShowPostedDaysCommand { get; }
    public IAsyncRelayCommand PushSelectedCommand { get; }
    public IAsyncRelayCommand PushAllPendingCommand { get; }

    public bool IsAdminUnlocked => _adminTokens?.IsUnlocked == true;
    public IAsyncRelayCommand RetrySelectedCommand { get; }
    public IAsyncRelayCommand VoidSelectedCommand { get; }
    public IAsyncRelayCommand ReviseSelectedCommand { get; }
    public IAsyncRelayCommand RepostSelectedCommand { get; }
    public IAsyncRelayCommand TallyPushSelectedCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }

    public IAsyncRelayCommand<BillListRowViewModel?> PushRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> RetryRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> RepostRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> ReviseRowCommand { get; }
    public IAsyncRelayCommand<BillListRowViewModel?> VoidRowCommand { get; }
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
    [ObservableProperty] private bool isPushingSelected;
    [ObservableProperty] private bool isRetryingSelected;
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
    public int PageTake => PageSize;
    public int VisibleCount => GroupedItems.Cast<object>().Count();
    public int Showing => VisibleCount;
    public int HiddenByPostedDaysCount => Math.Max(0, Items.Count - VisibleCount);
    public bool HasHiddenPostedDays => HideFullyPostedDays && HiddenByPostedDaysCount > 0;
    public bool IsFilteredEmpty => Items.Count > 0 && VisibleCount == 0;
    public int SelectedCount => _selectedCount;
    public bool HasSelection => SelectedCount > 0;
    public bool HasSingleSelection => SelectedCount == 1;
    public bool CanPrintSelected => HasSelection;
    public bool IsSelectionBarVisible => HasSelection;
    public string SelectionSummaryText => HasSelection ? $"{SelectedCount} selected" : "No selection";

    public bool AreAllVisibleSelected
    {
        get => Items.Count > 0 && Items.All(x => x.IsSelected);
        set => ToggleVisibleSelection(value);
    }

    public IReadOnlyList<Guid> SelectedBillIds =>
        Items.Where(x => x.IsSelected).Select(x => x.Id).ToArray();

    public BillListRowViewModel? SelectedRow =>
        Items.FirstOrDefault(x => x.IsSelected)
        ?? (SelectedBill is { } s ? Items.FirstOrDefault(x => x.Id == s.Id) : null);

    private bool IsActing => IsPushingSelected || IsRetryingSelected;

    partial void OnSkipChanged(int value) => RefreshComputed();
    partial void OnTotalChanged(int value) => RefreshComputed();
    partial void OnIsLoadingChanged(bool value) => RefreshComputed();
    partial void OnIsPushingSelectedChanged(bool value) => RefreshComputed();
    partial void OnIsRetryingSelectedChanged(bool value) => RefreshComputed();
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

        var ready = string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase)
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

        foreach (var item in Items)
        {
            item.IsSelected = ReferenceEquals(item, row);
        }

        _selectedCount = 1;
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
        foreach (var item in Items)
        {
            item.IsSelected = isSelected;
        }

        if (!isSelected)
        {
            _selectedCount = 0;
            SelectedBill = null;
        }
        else if (SelectedBill is null && Items.Count > 0)
        {
            _selectedCount = Items.Count;
            SelectedBill = Items[0].Item;
        }
        else
        {
            _selectedCount = Items.Count;
        }

        RefreshSelectionState();
    }

    public void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        _selectedCount = 0;
        SelectedBill = null;
        RefreshSelectionState();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_billsApi is null)
        {
            return;
        }

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

            ResetRows(list.Items);
            Total = list.Total;

            SelectedBill = BillsSelectionRestorer.Restore(Items, priorSelectedIds, priorFocusedId);
            RecalculateSelectedCount();
            RefreshSelectionState();

            StatusMessage = list.Total == 0
                ? "No bills found."
                : list.HasMore == true
                    ? $"Showing {Skip + 1}–{Skip + Items.Count}"
                    : $"Showing {Skip + 1}–{Skip + Items.Count} of {list.Total}";
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ResetRows(IReadOnlyList<BillSummaryItem> rows)
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnRowPropertyChanged;
        }

        Items.Clear();
        _selectedCount = 0;
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
        if (e.PropertyName != nameof(BillListRowViewModel.IsSelected))
        {
            return;
        }

        if (sender is not BillListRowViewModel row)
        {
            return;
        }

        if (row.IsSelected)
        {
            _selectedCount++;
            SelectedBill = row.Item;
        }
        else if (SelectedBill?.Id == row.Id && !Items.Any(x => x.IsSelected && x.Id == row.Id))
        {
            _selectedCount = Math.Max(0, _selectedCount - 1);
            SelectedBill = Items.FirstOrDefault(x => x.IsSelected)?.Item;
        }
        else if (!row.IsSelected)
        {
            _selectedCount = Math.Max(0, _selectedCount - 1);
        }

        RefreshSelectionState();
    }

    private void RecalculateSelectedCount() => _selectedCount = Items.Count(x => x.IsSelected);

    private void RefreshComputed()
    {
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(Showing));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HiddenByPostedDaysCount));
        OnPropertyChanged(nameof(HasHiddenPostedDays));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        RefreshSelectionState();
        RefreshCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        PrevPageCommand.NotifyCanExecuteChanged();
        PushAllPendingCommand.NotifyCanExecuteChanged();
        ShowPostedDaysCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(CanPrintSelected));
        OnPropertyChanged(nameof(IsSelectionBarVisible));
        OnPropertyChanged(nameof(AreAllVisibleSelected));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(SelectedBillIds));
        PushSelectedCommand.NotifyCanExecuteChanged();
        RetrySelectedCommand.NotifyCanExecuteChanged();
        VoidSelectedCommand.NotifyCanExecuteChanged();
        ReviseSelectedCommand.NotifyCanExecuteChanged();
        RepostSelectedCommand.NotifyCanExecuteChanged();
        TallyPushSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TallyPushButtonLabel));
        ClearSelectionCommand.NotifyCanExecuteChanged();
        PushRowCommand.NotifyCanExecuteChanged();
        RetryRowCommand.NotifyCanExecuteChanged();
        RepostRowCommand.NotifyCanExecuteChanged();
        ReviseRowCommand.NotifyCanExecuteChanged();
        VoidRowCommand.NotifyCanExecuteChanged();
        EditRowCommand.NotifyCanExecuteChanged();
        ChangeNumberRowCommand.NotifyCanExecuteChanged();
        MarkPostedRowCommand.NotifyCanExecuteChanged();
        MarkPostedSelectedCommand.NotifyCanExecuteChanged();
        MarkPendingRowCommand.NotifyCanExecuteChanged();
        MarkPendingSelectedCommand.NotifyCanExecuteChanged();
        DeleteRowCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefresh() => _billsApi is not null && !IsLoading && !IsActing;
    private bool CanNextPage() => HasNext && !IsLoading && !IsActing;
    private bool CanPrevPage() => Skip > 0 && !IsLoading && !IsActing;

    private bool CanPushAllPending() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && IsTallyPushAllowed;

    private async Task PushAllPendingAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.PushAllPendingAsync(cancellationToken);

    private bool CanPushSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && IsTallyPushAllowed
        && Items.Where(x => x.IsSelected).All(x => x.IsPendingLike);

    private bool CanRetrySelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && IsTallyPushAllowed
        && Items.Where(x => x.IsSelected).All(x => x.IsRetryable);

    public enum TallyPushMode { None, Push, Retry, Repost }

    private TallyPushMode GetTallyPushMode()
    {
        if (!HasSelection) return TallyPushMode.None;
        var selected = Items.Where(x => x.IsSelected).ToArray();
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

    private bool CanTallyPushSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && IsTallyPushAllowed
        && GetTallyPushMode() != TallyPushMode.None;

    private Task TallyPushSelectedAsync(CancellationToken cancellationToken) => GetTallyPushMode() switch
    {
        TallyPushMode.Push => PushSelectedAsync(cancellationToken),
        TallyPushMode.Retry => RetrySelectedAsync(cancellationToken),
        TallyPushMode.Repost => RepostSelectedAsync(cancellationToken),
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

    private void ClearFilters()
    {
        StateFilter = "All";
        FromDate = null;
        ToDate = null;
        SearchQuery = string.Empty;
        Skip = 0;
    }

    private async Task PushSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.PushSelectedAsync(cancellationToken);

    private async Task RetrySelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.RetrySelectedAsync(cancellationToken);

    private bool CanVoidSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && Items.Where(x => x.IsSelected).All(x => x.CanBeVoided);

    private async Task VoidSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.VoidSelectedAsync(cancellationToken);

    private bool CanReviseSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && Items.Where(x => x.IsSelected).All(x => x.CanBeRevised);

    private async Task ReviseSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.ReviseSelectedAsync(cancellationToken);

    private bool CanRepostSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && IsTallyPushAllowed
        && Items.Where(x => x.IsSelected).All(x => x.CanBeReposted);

    private async Task RepostSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.RepostSelectedAsync(cancellationToken);

    private async Task PushRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.PushRowAsync(row, cancellationToken);

    private async Task RetryRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.RetryRowAsync(row, cancellationToken);

    private async Task RepostRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.RepostRowAsync(row, cancellationToken);

    private async Task ReviseRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.ReviseRowAsync(row, cancellationToken);

    private async Task VoidRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.VoidRowAsync(row, cancellationToken);

    private void CopyInvoiceNumber(BillListRowViewModel? row)
        => _actionWorkflow.CopyInvoiceNumber(row);

    private async Task EditRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.EditRowAsync(row, cancellationToken);

    private async Task ChangeNumberRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.ChangeNumberRowAsync(row, cancellationToken);

    private async Task MarkPostedRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.MarkPostedRowAsync(row, cancellationToken);

    private bool CanMarkPostedFromContext(BillListRowViewModel? row) =>
        CanRunAdminContextAction(row, x => x.CanMarkPosted);

    private bool CanMarkPostedSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && Items.Where(x => x.IsSelected).All(x => x.CanMarkPosted);

    private async Task MarkPostedSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.MarkPostedSelectedAsync(cancellationToken);

    private async Task MarkPendingRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.MarkPendingRowAsync(row, cancellationToken);

    private bool CanMarkPendingFromContext(BillListRowViewModel? row) =>
        CanRunAdminContextAction(row, x => x.CanMarkPending);

    private bool CanMarkPendingSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && Items.Where(x => x.IsSelected).All(x => x.CanMarkPending);

    private async Task MarkPendingSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.MarkPendingSelectedAsync(cancellationToken);

    private async Task DeleteRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => await _actionWorkflow.DeleteRowAsync(row, cancellationToken);

    private bool CanDeleteFromContext(BillListRowViewModel? row) =>
        CanRunAdminContextAction(row, x => x.CanBeDeleted);

    private bool CanRunAdminContextAction(
        BillListRowViewModel? row,
        Func<BillListRowViewModel, bool> canRun)
    {
        if (_billsApi is null || IsLoading || IsActing || row is null)
        {
            return false;
        }

        var rows = row.IsSelected && SelectedCount > 1
            ? Items.Where(x => x.IsSelected).ToArray()
            : new[] { row };
        return rows.Length > 0 && rows.All(canRun);
    }

    private bool CanDeleteSelected() =>
        _billsApi is not null
        && !IsLoading
        && !IsActing
        && HasSelection
        && Items.Where(x => x.IsSelected).All(x => x.CanBeDeleted);

    private async Task DeleteSelectedAsync(CancellationToken cancellationToken)
        => await _actionWorkflow.DeleteSelectedAsync(cancellationToken);

}
