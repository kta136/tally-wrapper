using System.Windows;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal sealed class BillsBatchActionWorkflow(
    IBillsApiClient? billsApi,
    IBillsActionWorkflowHost host)
{
    internal async Task PushSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;

        var selectedIds = host.SelectedBillIds;
        if (selectedIds.Count == 0) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        host.IsPushingSelected = true;
        try
        {
            host.StatusMessage = $"Pushing {selectedIds.Count} selected bill(s)…";
            var response = await billsApi.PushSelectedAsync(
                new PushSelectedBillsRequest(selectedIds, null, "Selected push from Bills tab"),
                cancellationToken);
            host.StatusMessage = BillsStatusFormatter.FormatBatchPushStatus(response);
        }
        finally
        {
            host.IsPushingSelected = false;
        }

        await host.LoadAsync(cancellationToken);
    }

    internal async Task PushAllPendingAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;

        var confirm = MessageBox.Show(
            "Push every pending/draft bill to Tally? Bills are pushed oldest-first; the run stops on the first failure.",
            "Confirm Push All Pending",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        host.IsPushingSelected = true;
        try
        {
            host.StatusMessage = "Pushing all pending bills (oldest first)…";
            var response = await billsApi.PushPendingAsync(
                new PushPendingBillsRequest(null, "Push all pending from Bills tab", null),
                cancellationToken);
            host.StatusMessage = response.Matched == 0
                ? "No pending bills to push."
                : BillsStatusFormatter.FormatBatchPushStatus(response);
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Push all pending failed: {ex.Message}";
        }
        finally
        {
            host.IsPushingSelected = false;
        }

        await host.LoadAsync(cancellationToken);
    }

    internal async Task RetrySelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;

        var selectedIds = host.SelectedBillIds;
        if (selectedIds.Count == 0) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        host.IsRetryingSelected = true;
        try
        {
            var completed = 0;
            foreach (var billId in selectedIds)
            {
                host.StatusMessage = $"Retrying {completed + 1} of {selectedIds.Count}…";
                await billsApi.RetryAsync(billId, new RetryBillPostingRequest("Retry selected from Bills tab"), cancellationToken);
                completed++;
            }

            host.StatusMessage = $"Retried {completed} bill(s).";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Retry stopped: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }

        await host.LoadAsync(cancellationToken);
    }

    internal async Task VoidSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;

        var selectedRows = host.Items.Where(x => x.IsSelected).ToArray();
        if (selectedRows.Length == 0) return;

        var confirm = MessageBox.Show(
            $"Void {selectedRows.Length} bill(s)? This cannot be undone.",
            "Confirm Void",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        host.IsRetryingSelected = true;
        try
        {
            var completed = 0;
            foreach (var row in selectedRows)
            {
                host.StatusMessage = $"Voiding {completed + 1} of {selectedRows.Length}…";
                await billsApi.VoidAsync(row.Id, new VoidBillRequest("Voided from Bills tab"), cancellationToken);
                completed++;
            }
            host.StatusMessage = $"Voided {completed} bill(s).";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Void stopped: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }

        await host.LoadAsync(cancellationToken);
    }

    internal async Task ReviseSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = host.Items.Where(x => x.IsSelected).ToArray();
        if (rows.Length == 0) return;

        host.IsRetryingSelected = true;
        try
        {
            var completed = 0;
            foreach (var row in rows)
            {
                host.StatusMessage = $"Revising {completed + 1} of {rows.Length}…";
                await billsApi.ReviseAsync(row.Id, new ReviseBillRequest(null), cancellationToken);
                completed++;
            }
            host.StatusMessage = $"Revised {completed} bill(s).";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Revise stopped: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }

    internal async Task RepostSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = host.Items.Where(x => x.IsSelected).ToArray();
        if (rows.Length == 0) return;

        var confirm = MessageBox.Show(
            $"Repost {rows.Length} bill(s)? Each will queue another Tally posting.",
            "Confirm Repost",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        host.IsRetryingSelected = true;
        try
        {
            var completed = 0;
            foreach (var row in rows)
            {
                host.StatusMessage = $"Reposting {completed + 1} of {rows.Length}…";
                await billsApi.RepostAsync(row.Id, new RepostBillRequest(Guid.NewGuid().ToString("N"), "Batch repost from Bills tab"), cancellationToken);
                completed++;
            }
            host.StatusMessage = $"Reposted {completed} bill(s).";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Repost stopped: {ex.Message}";
        }
        finally
        {
            host.IsRetryingSelected = false;
        }
        await host.LoadAsync(cancellationToken);
    }
}
