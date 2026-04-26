using System.Windows;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal sealed class BillsRowActionWorkflow(
    IBillsApiClient? billsApi,
    IBillsActionWorkflowHost host)
{
    internal async Task PushRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        host.IsPushingSelected = true;
        try
        {
            host.StatusMessage = $"Pushing {row.InvoiceNumberDisplay}…";
            await billsApi.PushAsync(row.Id, new PushBillRequest(null, "Context menu push"), cancellationToken);
            host.StatusMessage = $"Pushed {row.InvoiceNumberDisplay}.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Push failed: {ex.Message}";
        }
        finally
        {
            host.IsPushingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task RetryRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Retrying {row.InvoiceNumberDisplay}…";
            await billsApi.RetryAsync(row.Id, new RetryBillPostingRequest("Context menu retry"), cancellationToken);
            host.StatusMessage = $"Retried {row.InvoiceNumberDisplay}.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Retry failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task RepostRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;

        var confirm = MessageBox.Show(
            $"Repost {row.InvoiceNumberDisplay}? This will push another voucher to Tally.",
            "Confirm Repost",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Reposting {row.InvoiceNumberDisplay}…";
            await billsApi.RepostAsync(row.Id, new RepostBillRequest(Guid.NewGuid().ToString("N"), "Context menu repost"), cancellationToken);
            host.StatusMessage = $"Reposted {row.InvoiceNumberDisplay}.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Repost failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task ReviseRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Creating revision for {row.InvoiceNumberDisplay}…";
            var revised = await billsApi.ReviseAsync(row.Id, new ReviseBillRequest(null), cancellationToken);
            host.StatusMessage = $"Revision created: {revised.InvoiceNumber ?? "(pending)"}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Revise failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task VoidRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;

        var confirm = MessageBox.Show(
            $"Void {row.InvoiceNumberDisplay}? This cannot be undone.",
            "Confirm Void",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Voiding {row.InvoiceNumberDisplay}…";
            await billsApi.VoidAsync(row.Id, new VoidBillRequest("Voided from Bills tab"), cancellationToken);
            host.StatusMessage = $"Voided {row.InvoiceNumberDisplay}.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Void failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal void CopyInvoiceNumber(BillListRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.InvoiceNumber)) return;
        try
        {
            Clipboard.SetText(row.InvoiceNumber!);
            host.StatusMessage = $"Copied invoice number: {row.InvoiceNumber}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    internal async Task EditRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null || host.EditBillHandler is null) return;
        try
        {
            await host.EditBillHandler(row.Id, cancellationToken);
            host.StatusMessage = $"Editing {row.InvoiceNumberDisplay} in the Invoice tab.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Edit failed: {ex.Message}";
        }
    }
}
