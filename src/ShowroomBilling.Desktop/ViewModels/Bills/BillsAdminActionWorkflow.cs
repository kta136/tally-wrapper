using System.Windows;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal sealed class BillsAdminActionWorkflow(
    IBillsApiClient? billsApi,
    BillsAdminTokenGate adminTokenGate,
    IBillsActionWorkflowHost host)
{
    internal async Task ChangeNumberRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        if (host.ChangeNumberPromptHandler is null)
        {
            host.StatusMessage = "Change number dialog is not wired.";
            return;
        }

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        var (confirmed, newNumber, reason) = await host.ChangeNumberPromptHandler(row, cancellationToken);
        if (!confirmed || string.IsNullOrWhiteSpace(newNumber)) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Changing number of {row.InvoiceNumberDisplay} → {newNumber}…";
            await billsApi.ChangeInvoiceNumberAsync(
                row.Id,
                new ChangeBillNumberRequest(newNumber, reason, DryRun: false),
                token,
                cancellationToken);
            host.StatusMessage = $"Invoice number changed to {newNumber}.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Change number failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task MarkPostedRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Marking {row.InvoiceNumberDisplay} as pushed…";
            await billsApi.MarkPostedAsync(row.Id, new MarkBillStateRequest(null), token, cancellationToken);
            host.StatusMessage = $"Marked {row.InvoiceNumberDisplay} as pushed.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Mark pushed failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task MarkPendingRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Marking {row.InvoiceNumberDisplay} as pending…";
            await billsApi.MarkPendingAsync(row.Id, new MarkBillStateRequest(null), token, cancellationToken);
            host.StatusMessage = $"Marked {row.InvoiceNumberDisplay} as pending.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Mark pending failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task DeleteRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        DeleteBillResponse? dry;
        try
        {
            dry = await billsApi.DeleteAsync(row.Id, new DeleteBillRequest(null, DryRun: true), token, cancellationToken);
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Delete check failed: {ex.Message}";
            return;
        }

        var prompt = dry.TallyDiverges
            ? $"{row.InvoiceNumberDisplay} was posted to Tally ({row.State}). Deleting locally will NOT remove it from Tally — reconcile there manually. Continue?"
            : $"Permanently delete {row.InvoiceNumberDisplay}? This cannot be undone.";
        var confirm = MessageBox.Show(
            prompt,
            "Confirm Delete",
            MessageBoxButton.OKCancel,
            dry.TallyDiverges ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Deleting {row.InvoiceNumberDisplay}…";
            await billsApi.DeleteAsync(row.Id, new DeleteBillRequest("Deleted from Bills tab", DryRun: false), token, cancellationToken);
            host.StatusMessage = $"Deleted {row.InvoiceNumberDisplay}.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = host.Items.Where(x => x.IsSelected).ToArray();
        if (rows.Length == 0) return;

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        var anyPosted = rows.Any(r => r.State is "posted" or "failed");
        var prompt = anyPosted
            ? $"Permanently delete {rows.Length} bill(s)? Some were posted to Tally and will NOT be removed there — reconcile manually."
            : $"Permanently delete {rows.Length} bill(s)? This cannot be undone.";
        var confirm = MessageBox.Show(
            prompt,
            "Confirm Delete",
            MessageBoxButton.OKCancel,
            anyPosted ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        host.IsRetryingSelected = true;
        try
        {
            host.StatusMessage = $"Deleting {rows.Length} bill(s)…";
            var result = await billsApi.DeleteSelectedAsync(
                new DeleteSelectedBillsRequest(rows.Select(r => r.Id).ToArray(), "Batch delete from Bills tab"),
                token,
                cancellationToken);
            host.StatusMessage = result.Skipped == 0
                ? $"Deleted {result.Deleted} bill(s)."
                : $"Deleted {result.Deleted}; skipped {result.Skipped}. See details in the audit log.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    private async Task<(string Token, string Reason)?> PromptForAdminReasonAsync(
        BillListRowViewModel? row,
        string title,
        Func<BillListRowViewModel, string> messageFactory,
        CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return null;
        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return null;
        if (host.ReasonPromptHandler is null)
        {
            host.StatusMessage = "Reason prompt is not wired.";
            return null;
        }

        var reason = await host.ReasonPromptHandler(title, messageFactory(row), cancellationToken);
        return reason is null ? null : (token, reason);
    }

    private async Task<string?> EnsureAdminTokenAsync(CancellationToken cancellationToken)
    {
        var (token, statusMessage) = await adminTokenGate.EnsureAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            host.StatusMessage = statusMessage;
        }
        return token;
    }
}
