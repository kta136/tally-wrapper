using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>Admin-only actions: change number, mark pushed / pending, delete.</summary>
internal sealed class BillsAdminActionWorkflow(
    IBillsApiClient? billsApi,
    BillsAdminTokenGate adminTokenGate,
    IBillsActionWorkflowHost host,
    BillsActionRunner runner)
{
    /// <summary>The two "mark" actions differ only in these words and in the call they make.</summary>
    private sealed record MarkAction(
        string Word,
        string Title,
        string Blocked,
        string Effect,
        string Check,
        Func<BillListRowViewModel, bool> Allowed,
        Func<IBillsApiClient, Guid, MarkBillStateRequest, string, CancellationToken, Task> Call);

    private static readonly MarkAction MarkPosted = new(
        "pushed",
        "Mark as pushed",
        "Mark pushed blocked: every selected bill must be pending, draft, or failed.",
        "as posted **without contacting Tally**.",
        "Do it only after you have checked that the voucher is already in Tally.",
        row => row.CanMarkPosted,
        (api, id, request, token, ct) => api.MarkPostedAsync(id, request, token, ct));

    private static readonly MarkAction MarkPending = new(
        "pending",
        "Mark as pending",
        "Mark pending blocked: every selected bill must be posted or failed.",
        "as pending **without changing Tally**.",
        "Do it only after you have checked what Tally holds for it.",
        row => row.CanMarkPending,
        (api, id, request, token, ct) => api.MarkPendingAsync(id, request, token, ct));

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

        await runner.RunAsync(
            $"Changing number of {row.InvoiceNumberDisplay} → {newNumber}…",
            "Change number failed",
            async () =>
            {
                await billsApi.ChangeInvoiceNumberAsync(
                    row.Id,
                    new ChangeBillNumberRequest(newNumber, reason, DryRun: false),
                    token,
                    cancellationToken);
                return $"Invoice number changed to {newNumber}.";
            },
            cancellationToken);
    }

    internal Task MarkPostedRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => MarkRowsAsync(MarkPosted, runner.ContextRows(row), cancellationToken);

    internal Task MarkPostedSelectedAsync(CancellationToken cancellationToken)
        => MarkRowsAsync(MarkPosted, runner.SelectedRows(), cancellationToken);

    internal Task MarkPendingRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
        => MarkRowsAsync(MarkPending, runner.ContextRows(row), cancellationToken);

    internal Task MarkPendingSelectedAsync(CancellationToken cancellationToken)
        => MarkRowsAsync(MarkPending, runner.SelectedRows(), cancellationToken);

    private async Task MarkRowsAsync(
        MarkAction mark,
        IReadOnlyList<BillListRowViewModel> rows,
        CancellationToken cancellationToken)
    {
        if (billsApi is null || rows.Count == 0) return;
        if (!rows.All(mark.Allowed))
        {
            host.StatusMessage = mark.Blocked;
            return;
        }

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;
        if (host.ReasonPromptHandler is null)
        {
            host.StatusMessage = "Reason prompt is not wired.";
            return;
        }

        var subject = rows.Count == 1 ? rows[0].InvoiceNumberDisplay : $"{rows.Count} selected bills";
        var total = UiCulture.Rupees(rows.Sum(row => row.GrandTotal));
        var context = rows.Count == 1
            ? $"{subject} · {rows[0].PartyName} · ₹{total}"
            : $"{subject} · ₹{total}";
        var reason = await host.ReasonPromptHandler(
            new ReasonRequest(mark.Title, context, $"This marks {subject} {mark.Effect} {mark.Check}", mark.Title),
            cancellationToken);
        if (reason is null) return;

        await runner.ForEachRowAsync(
            rows,
            "Marking",
            $"Mark {mark.Word} stopped",
            row => mark.Call(billsApi, row.Id, new MarkBillStateRequest(reason), token, cancellationToken),
            count => count == 1
                ? $"Marked {rows[0].InvoiceNumberDisplay} as {mark.Word}."
                : $"Marked {count} bill(s) as {mark.Word}.",
            cancellationToken);
    }

    internal async Task DeleteRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        if (runner.ContextRows(row).Count > 1)
        {
            await DeleteSelectedAsync(cancellationToken);
            return;
        }

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        DeleteBillResponse dry;
        try
        {
            dry = await billsApi.DeleteAsync(row.Id, new DeleteBillRequest(null, DryRun: true), token, cancellationToken);
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Delete check failed: {ApiResponseReader.FormatError(ex)}";
            return;
        }

        var question = dry.TallyDiverges
            ? $"{row.InvoiceNumberDisplay} was posted to Tally ({row.State}). Deleting it here does not remove it from Tally; reconcile it there by hand."
            : "This removes the bill from this database and frees its number if it was the latest. Its history stays in the audit log. Nothing is deleted in Tally.";
        if (!await runner.ConfirmAsync(new ConfirmRequest(
                $"Delete {row.InvoiceNumberDisplay}?", question, "Delete bill", IsDestructive: true, Note: "This cannot be undone."))) return;

        await runner.RunAsync(
            $"Deleting {row.InvoiceNumberDisplay}…",
            "Delete failed",
            async () =>
            {
                await billsApi.DeleteAsync(row.Id, new DeleteBillRequest("Deleted from Bills tab", DryRun: false), token, cancellationToken);
                return $"Deleted {row.InvoiceNumberDisplay}.";
            },
            cancellationToken);
    }

    internal async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = runner.SelectedRows();
        if (rows.Count == 0) return;

        var token = await EnsureAdminTokenAsync(cancellationToken);
        if (token is null) return;

        var question = rows.Any(row => BillStateCapabilities.TallyDivergesIfDeleted(row.State))
            ? "Some of these bills were posted to Tally. Deleting them here does not remove them from Tally; reconcile those there by hand."
            : "This removes the bills from this database and frees their numbers where it can. Their history stays in the audit log. Nothing is deleted in Tally.";
        if (!await runner.ConfirmAsync(new ConfirmRequest(
                $"Delete {rows.Count} bill(s)?", question, "Delete bills", IsDestructive: true, Note: "This cannot be undone."))) return;

        await runner.RunAsync(
            $"Deleting {rows.Count} bill(s)…",
            "Delete failed",
            async () =>
            {
                var result = await billsApi.DeleteSelectedAsync(
                    new DeleteSelectedBillsRequest(rows.Select(r => r.Id).ToArray(), "Batch delete from Bills tab"),
                    token,
                    cancellationToken);
                return result.Skipped == 0
                    ? $"Deleted {result.Deleted} bill(s)."
                    : $"Deleted {result.Deleted}; skipped {result.Skipped}. See details in the audit log.";
            },
            cancellationToken);
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
