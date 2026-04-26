using System.Collections.ObjectModel;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal interface IBillsActionWorkflowHost
{
    ObservableCollection<BillListRowViewModel> Items { get; }
    IReadOnlyList<Guid> SelectedBillIds { get; }
    string StatusMessage { get; set; }
    bool IsPushingSelected { get; set; }
    bool IsRetryingSelected { get; set; }
    Func<Guid, CancellationToken, Task>? EditBillHandler { get; }
    Func<BillListRowViewModel, CancellationToken, Task<(bool Confirmed, string NewNumber, string? Reason)>>? ChangeNumberPromptHandler { get; }
    Func<string, string, CancellationToken, Task<string?>>? ReasonPromptHandler { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
}

internal sealed class BillsActionWorkflow(
    IBillsApiClient? billsApi,
    BillsAdminTokenGate adminTokenGate,
    IBillsActionWorkflowHost host)
{
    private readonly BillsBatchActionWorkflow _batchActions = new(billsApi, host);
    private readonly BillsRowActionWorkflow _rowActions = new(billsApi, host);
    private readonly BillsAdminActionWorkflow _adminActions = new(billsApi, adminTokenGate, host);

    public async Task PushSelectedAsync(CancellationToken cancellationToken)
    {
        await _batchActions.PushSelectedAsync(cancellationToken);
    }

    public async Task RetrySelectedAsync(CancellationToken cancellationToken)
    {
        await _batchActions.RetrySelectedAsync(cancellationToken);
    }

    public async Task VoidSelectedAsync(CancellationToken cancellationToken)
    {
        await _batchActions.VoidSelectedAsync(cancellationToken);
    }

    public async Task ReviseSelectedAsync(CancellationToken cancellationToken)
    {
        await _batchActions.ReviseSelectedAsync(cancellationToken);
    }

    public async Task RepostSelectedAsync(CancellationToken cancellationToken)
    {
        await _batchActions.RepostSelectedAsync(cancellationToken);
    }

    public async Task PushRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _rowActions.PushRowAsync(row, cancellationToken);
    }

    public async Task RetryRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _rowActions.RetryRowAsync(row, cancellationToken);
    }

    public async Task RepostRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _rowActions.RepostRowAsync(row, cancellationToken);
    }

    public async Task ReviseRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _rowActions.ReviseRowAsync(row, cancellationToken);
    }

    public async Task VoidRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _rowActions.VoidRowAsync(row, cancellationToken);
    }

    public void CopyInvoiceNumber(BillListRowViewModel? row)
    {
        _rowActions.CopyInvoiceNumber(row);
    }

    public async Task EditRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _rowActions.EditRowAsync(row, cancellationToken);
    }

    public async Task ChangeNumberRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _adminActions.ChangeNumberRowAsync(row, cancellationToken);
    }

    public async Task MarkPostedRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _adminActions.MarkPostedRowAsync(row, cancellationToken);
    }

    public async Task MarkPendingRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _adminActions.MarkPendingRowAsync(row, cancellationToken);
    }

    public async Task DeleteRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await _adminActions.DeleteRowAsync(row, cancellationToken);
    }

    public async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        await _adminActions.DeleteSelectedAsync(cancellationToken);
    }
}
