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
        await MarkPostedRowsAsync(GetContextRows(row), cancellationToken);
    }

    internal async Task MarkPostedSelectedAsync(CancellationToken cancellationToken)
    {
        await MarkPostedRowsAsync(host.Items.Where(x => x.IsSelected).ToArray(), cancellationToken);
    }

    internal async Task MarkPendingRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        await MarkPendingRowsAsync(GetContextRows(row), cancellationToken);
    }

    internal async Task MarkPendingSelectedAsync(CancellationToken cancellationToken)
    {
        await MarkPendingRowsAsync(host.Items.Where(x => x.IsSelected).ToArray(), cancellationToken);
    }

    private async Task MarkPostedRowsAsync(IReadOnlyList<BillListRowViewModel> rows, CancellationToken cancellationToken)
    {
        if (billsApi is null || rows.Count == 0) return;
        if (rows.Any(row => !row.CanMarkPosted))
        {
            host.StatusMessage = "Mark pushed blocked: every selected bill must be pending, draft, or failed.";
            return;
        }

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;
        var reason = await PromptForAdminReasonAsync(
            rows,
            "Mark as Pushed",
            rows.Count == 1
                ? $"{rows[0].InvoiceNumberDisplay} will be marked posted locally without calling Tally."
                : $"{rows.Count} selected bills will be marked posted locally without calling Tally.",
            cancellationToken);
        if (reason is null) return;

        host.IsRetryingSelected = true;
        try
        {
            var completed = 0;
            foreach (var row in rows)
            {
                host.StatusMessage = $"Marking {completed + 1} of {rows.Count} as pushed…";
                await billsApi.MarkPostedAsync(row.Id, new MarkBillStateRequest(reason), token, cancellationToken);
                completed++;
            }

            host.StatusMessage = rows.Count == 1
                ? $"Marked {rows[0].InvoiceNumberDisplay} as pushed."
                : $"Marked {completed} bill(s) as pushed.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Mark pushed stopped: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    private async Task MarkPendingRowsAsync(IReadOnlyList<BillListRowViewModel> rows, CancellationToken cancellationToken)
    {
        if (billsApi is null || rows.Count == 0) return;
        if (rows.Any(row => !row.CanMarkPending))
        {
            host.StatusMessage = "Mark pending blocked: every selected bill must be posted or failed.";
            return;
        }

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;
        var reason = await PromptForAdminReasonAsync(
            rows,
            "Mark as Pending",
            rows.Count == 1
                ? $"{rows[0].InvoiceNumberDisplay} will be marked pending locally without changing Tally."
                : $"{rows.Count} selected bills will be marked pending locally without changing Tally.",
            cancellationToken);
        if (reason is null) return;

        host.IsRetryingSelected = true;
        try
        {
            var completed = 0;
            foreach (var row in rows)
            {
                host.StatusMessage = $"Marking {completed + 1} of {rows.Count} as pending…";
                await billsApi.MarkPendingAsync(row.Id, new MarkBillStateRequest(reason), token, cancellationToken);
                completed++;
            }

            host.StatusMessage = rows.Count == 1
                ? $"Marked {rows[0].InvoiceNumberDisplay} as pending."
                : $"Marked {completed} bill(s) as pending.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Mark pending stopped: {ex.Message}";
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
        var contextRows = GetContextRows(row);
        if (contextRows.Count > 1)
        {
            await DeleteSelectedAsync(cancellationToken);
            return;
        }

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

        var anyPosted = rows.Any(r => BillStateCapabilities.TallyDivergesIfDeleted(r.State));
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

    private async Task<string?> PromptForAdminReasonAsync(
        IReadOnlyList<BillListRowViewModel> rows,
        string title,
        string message,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return null;
        if (host.ReasonPromptHandler is null)
        {
            host.StatusMessage = "Reason prompt is not wired.";
            return null;
        }

        return await host.ReasonPromptHandler(title, message, cancellationToken);
    }

    private IReadOnlyList<BillListRowViewModel> GetContextRows(BillListRowViewModel? row)
    {
        if (row is null)
        {
            return Array.Empty<BillListRowViewModel>();
        }

        return row.IsSelected && host.Items.Count(x => x.IsSelected) > 1
            ? host.Items.Where(x => x.IsSelected).ToArray()
            : new[] { row };
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
