using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

public partial class BillDetailsViewModel : ObservableObject, IBillDetailsActionHost
{
    private readonly IBillsApiClient? _billsApi;
    private readonly BillDetailsActionWorkflow _actions;

    public BillDetailsViewModel() : this(null) { }

    public BillDetailsViewModel(IBillsApiClient? billsApi)
    {
        _billsApi = billsApi;
        _actions = new BillDetailsActionWorkflow(_billsApi, this);
        AuditTrail = new ObservableCollection<BillAuditTrailItemViewModel>();

        PushCommand = new AsyncRelayCommand(_actions.PushAsync, () => CanAct(BillStateCapabilities.CanPush));
        RetryCommand = new AsyncRelayCommand(_actions.RetryAsync, () => CanAct(BillStateCapabilities.CanRetry));
        RepostCommand = new AsyncRelayCommand(_actions.RepostAsync, () => CanAct(BillStateCapabilities.CanRepost));
        VoidCommand = new AsyncRelayCommand(_actions.VoidAsync, () => CanAct(BillStateCapabilities.CanVoid));
        ReviseCommand = new AsyncRelayCommand(_actions.ReviseAsync, () => CanAct(BillStateCapabilities.CanRevise));
    }

    public ObservableCollection<BillAuditTrailItemViewModel> AuditTrail { get; }

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isActing;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private BillResponse? bill;

    public IAsyncRelayCommand PushCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RepostCommand { get; }
    public IAsyncRelayCommand VoidCommand { get; }
    public IAsyncRelayCommand ReviseCommand { get; }

    public Action? BillMutated { get; set; }

    /// <summary>Handler the shell wires up to ask a yes/no question in the confirm overlay.</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    public IReadOnlyList<BillLineItemDto> Lines =>
        Bill?.CurrentRevision?.Payload.Lines ?? Array.Empty<BillLineItemDto>();

    public BillPayloadDto? Payload => Bill?.CurrentRevision?.Payload;
    public BillTotalsDto? Totals => Payload?.Totals;
    public bool HasAuditTrail => AuditTrail.Count > 0;
    public bool IsPendingLike => BillStateCapabilities.IsPendingLike(Bill?.State);
    public bool IsPosted => BillStateCapabilities.IsPosted(Bill?.State);
    public bool IsFailed => BillStateCapabilities.IsFailed(Bill?.State);
    public bool CanVoidNow => BillStateCapabilities.CanVoid(Bill?.State);
    public bool CanReviseNow => BillStateCapabilities.CanRevise(Bill?.State);
    public string StateChipLabel => BillStateDisplay.Label(Bill?.State, Bill?.EditedAfterPush ?? false);

    public string StateTone => BillStateDisplay.Tone(Bill?.State);

    public string InvoiceNumberDisplay => string.IsNullOrWhiteSpace(Bill?.InvoiceNumber) ? "Assigned on push" : Bill!.InvoiceNumber!;
    public string AuditEmptyMessage => IsLoading ? "Loading timeline…" : "No timeline events yet.";

    public BillPrintContent? BuildPrintContent(
        CompanyProfile company,
        IReadOnlyList<KaratMasterEntry>? karatMappings = null)
    {
        if (Bill is null || Payload is null || Totals is null)
        {
            return null;
        }

        return BillDetailsPrintMapper.CreatePrintContent(Bill, company, karatMappings);
    }

    public static BillPrintContent? CreatePrintContent(
        BillResponse bill,
        CompanyProfile company,
        IReadOnlyList<KaratMasterEntry>? karatMappings = null)
        => BillDetailsPrintMapper.CreatePrintContent(bill, company, karatMappings);

    partial void OnBillChanged(BillResponse? value)
    {
        OnPropertyChanged(nameof(Lines));
        OnPropertyChanged(nameof(Payload));
        OnPropertyChanged(nameof(Totals));
        OnPropertyChanged(nameof(IsPendingLike));
        OnPropertyChanged(nameof(IsPosted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(CanVoidNow));
        OnPropertyChanged(nameof(CanReviseNow));
        OnPropertyChanged(nameof(InvoiceNumberDisplay));
        OnPropertyChanged(nameof(StateChipLabel));
        OnPropertyChanged(nameof(StateTone));
        NotifyActionsChanged();
    }

    partial void OnIsActingChanged(bool value) => NotifyActionsChanged();

    private void NotifyActionsChanged()
    {
        PushCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RepostCommand.NotifyCanExecuteChanged();
        VoidCommand.NotifyCanExecuteChanged();
        ReviseCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct(Func<string?, bool> allowedInState)
        => _billsApi is not null && Bill is not null && !IsActing && !IsLoading && allowedInState(Bill.State);

    public async Task LoadAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        if (_billsApi is null)
        {
            StatusMessage = "Bills API unavailable.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading bill…";
        Bill = null;
        AuditTrail.Clear();
        OnPropertyChanged(nameof(HasAuditTrail));
        OnPropertyChanged(nameof(AuditEmptyMessage));

        try
        {
            // The timeline is read by bill id, so it loads alongside the bill
            // instead of after it.
            var supporting = ReloadSupportingStateAsync(billId, cancellationToken);
            var bill = await _billsApi.GetAsync(billId, cancellationToken);
            await supporting;
            if (bill is null)
            {
                ApplyAudit(null);
                StatusMessage = "Bill not found.";
                return;
            }

            Bill = bill;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(AuditEmptyMessage));
            NotifyActionsChanged();
        }
    }

    public async Task ReloadSupportingStateAsync(Guid billId, CancellationToken cancellationToken)
    {
        if (_billsApi is null)
        {
            return;
        }

        // The timeline may fail on its own; the bill itself is still worth showing.
        try
        {
            ApplyAudit(await _billsApi.GetAuditAsync(billId, cancellationToken));
        }
        catch
        {
            ApplyAudit(null);
        }
    }

    private void ApplyAudit(BillAuditResponse? audit)
    {
        AuditTrail.Clear();
        if (audit?.Events is null)
        {
            OnPropertyChanged(nameof(HasAuditTrail));
            return;
        }

        foreach (var item in audit.Events.Select(BillAuditTrailMapper.Map))
        {
            AuditTrail.Add(item);
        }

        OnPropertyChanged(nameof(HasAuditTrail));
        OnPropertyChanged(nameof(AuditEmptyMessage));
    }
}
