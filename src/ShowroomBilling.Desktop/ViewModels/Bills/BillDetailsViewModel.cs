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
    private readonly IBillsApiClient _billsApi;
    private readonly BillDetailsActionWorkflow _actions;

    public BillDetailsViewModel(IBillsApiClient billsApi, IDialogService dialogs)
    {
        _billsApi = billsApi;
        _actions = new BillDetailsActionWorkflow(_billsApi, this, dialogs);
        AuditTrail = new ObservableCollection<BillAuditTrailItemViewModel>();
        AuditTrail.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Timeline));
    }

    public ObservableCollection<BillAuditTrailItemViewModel> AuditTrail { get; }

    // The five actions depend on the bill's state and on nothing being in flight.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuditEmptyMessage))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(RetryCommand), nameof(RepostCommand), nameof(VoidCommand), nameof(ReviseCommand))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(RetryCommand), nameof(RepostCommand), nameof(VoidCommand), nameof(ReviseCommand))]
    private bool isActing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FooterText))]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(RetryCommand), nameof(RepostCommand), nameof(VoidCommand), nameof(ReviseCommand))]
    private BillResponse? bill;

    public Action? BillMutated { get; set; }

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

    public string StateKind => BillStateDisplay.Kind(Bill?.State);

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
        OnPropertyChanged(nameof(StateKind));
        NotifyDisplayChanged();
    }

    private bool CanAct(Func<string?, bool> allowedInState)
        => Bill is not null && !IsActing && !IsLoading && allowedInState(Bill.State);

    private bool CanPush => CanAct(BillStateCapabilities.CanPush);

    private bool CanRetry => CanAct(BillStateCapabilities.CanRetry);

    private bool CanRepost => CanAct(BillStateCapabilities.CanRepost);

    private bool CanVoid => CanAct(BillStateCapabilities.CanVoid);

    private bool CanRevise => CanAct(BillStateCapabilities.CanRevise);

    [RelayCommand(CanExecute = nameof(CanPush))]
    private Task PushAsync(CancellationToken cancellationToken) => _actions.PushAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync(CancellationToken cancellationToken) => _actions.RetryAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRepost))]
    private Task RepostAsync(CancellationToken cancellationToken) => _actions.RepostAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanVoid))]
    private Task VoidAsync(CancellationToken cancellationToken) => _actions.VoidAsync(cancellationToken);

    [RelayCommand(CanExecute = nameof(CanRevise))]
    private Task ReviseAsync(CancellationToken cancellationToken) => _actions.ReviseAsync(cancellationToken);

    public async Task LoadAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusMessage = "Loading bill…";
        Bill = null;
        AuditTrail.Clear();
        OnPropertyChanged(nameof(HasAuditTrail));

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
        }
    }

    public async Task ReloadSupportingStateAsync(Guid billId, CancellationToken cancellationToken)
    {
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
