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

        PushCommand = new AsyncRelayCommand(PushAsync, CanPush);
        RetryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        RepostCommand = new AsyncRelayCommand(RepostAsync, CanRepost);
        VoidCommand = new AsyncRelayCommand(VoidAsync, CanVoid);
        ReviseCommand = new AsyncRelayCommand(ReviseAsync, CanRevise);
    }

    public ObservableCollection<BillAuditTrailItemViewModel> AuditTrail { get; }

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isActing;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private BillResponse? bill;
    [ObservableProperty] private BillPostingStatusResponse? postingStatus;

    public IAsyncRelayCommand PushCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand RepostCommand { get; }
    public IAsyncRelayCommand VoidCommand { get; }
    public IAsyncRelayCommand ReviseCommand { get; }

    public Action? BillMutated { get; set; }

    public IReadOnlyList<BillLineItemDto> Lines =>
        Bill?.CurrentRevision?.Payload.Lines ?? Array.Empty<BillLineItemDto>();

    public BillPayloadDto? Payload => Bill?.CurrentRevision?.Payload;
    public BillTotalsDto? Totals => Payload?.Totals;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasAuditTrail => AuditTrail.Count > 0;
    public bool IsPendingLike => BillStateCapabilities.IsPendingLike(Bill?.State);
    public bool IsPosted => BillStateCapabilities.IsPosted(Bill?.State);
    public bool IsFailed => BillStateCapabilities.IsFailed(Bill?.State);
    public bool CanVoidNow => BillStateCapabilities.CanVoid(Bill?.State);
    public bool CanReviseNow => BillStateCapabilities.CanRevise(Bill?.State);
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
        NotifyActionsChanged();
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));
    partial void OnIsActingChanged(bool value) => NotifyActionsChanged();

    private void NotifyActionsChanged()
    {
        PushCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RepostCommand.NotifyCanExecuteChanged();
        VoidCommand.NotifyCanExecuteChanged();
        ReviseCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct() => _billsApi is not null && Bill is not null && !IsActing && !IsLoading;
    private bool CanPush() => CanAct() && BillStateCapabilities.CanPush(Bill!.State);
    private bool CanRetry() => CanAct() && BillStateCapabilities.CanRetry(Bill!.State);
    private bool CanRepost() => CanAct() && BillStateCapabilities.CanRepost(Bill!.State);
    private bool CanVoid() => CanAct() && BillStateCapabilities.CanVoid(Bill!.State);
    private bool CanRevise() => CanAct() && BillStateCapabilities.CanRevise(Bill!.State);

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
        PostingStatus = null;
        AuditTrail.Clear();
        OnPropertyChanged(nameof(HasAuditTrail));
        OnPropertyChanged(nameof(AuditEmptyMessage));

        try
        {
            var bill = await _billsApi.GetAsync(billId, cancellationToken);
            if (bill is null)
            {
                StatusMessage = "Bill not found.";
                return;
            }

            Bill = bill;
            try
            {
                PostingStatus = await _billsApi.GetPostingStatusAsync(billId, cancellationToken);
            }
            catch
            {
                PostingStatus = null;
            }

            try
            {
                var audit = await _billsApi.GetAuditAsync(billId, cancellationToken);
                ApplyAudit(audit);
            }
            catch
            {
                AuditTrail.Clear();
                OnPropertyChanged(nameof(HasAuditTrail));
            }

            StatusMessage = string.Empty;
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
            OnPropertyChanged(nameof(AuditEmptyMessage));
            NotifyActionsChanged();
        }
    }

    private async Task PushAsync(CancellationToken cancellationToken)
        => await _actions.PushAsync(cancellationToken);

    private async Task RetryAsync(CancellationToken cancellationToken)
        => await _actions.RetryAsync(cancellationToken);

    private async Task RepostAsync(CancellationToken cancellationToken)
        => await _actions.RepostAsync(cancellationToken);

    private async Task VoidAsync(CancellationToken cancellationToken)
        => await _actions.VoidAsync(cancellationToken);

    private async Task ReviseAsync(CancellationToken cancellationToken)
        => await _actions.ReviseAsync(cancellationToken);

    public async Task ReloadSupportingStateAsync(Guid billId, CancellationToken cancellationToken)
    {
        try
        {
            PostingStatus = await _billsApi!.GetPostingStatusAsync(billId, cancellationToken);
        }
        catch
        {
            PostingStatus = null;
        }

        try
        {
            ApplyAudit(await _billsApi!.GetAuditAsync(billId, cancellationToken));
        }
        catch
        {
            AuditTrail.Clear();
            OnPropertyChanged(nameof(HasAuditTrail));
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
