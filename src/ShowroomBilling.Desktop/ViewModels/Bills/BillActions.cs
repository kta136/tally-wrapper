using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>A bill an action is for: its id, what the questions and the status line call it, and its state.</summary>
internal sealed record BillRef(Guid Id, string Name, string? State)
{
    public bool IsPosted => BillStateCapabilities.IsPosted(State);

    public static BillRef Of(BillListRowViewModel row) => new(row.Id, row.InvoiceNumberDisplay, row.State);

    public static BillRef Of(BillResponse bill) => new(bill.Id, $"invoice {bill.InvoiceNumber ?? "—"}", bill.State);
}

/// <summary>
/// What an action came to. An action returns null instead when the operator said no, or it could
/// not start and the screen has already been told why: nothing happened and there is nothing to add.
/// </summary>
/// <param name="Message">For the status line.</param>
/// <param name="Sent">
/// False when the action stopped before it sent anything that changes a bill (a check refused it),
/// so the screen has nothing to reload.
/// </param>
/// <param name="Bill">The bill as the server returned it, when the call returns one. After Revise it is the new bill.</param>
internal sealed record BillActionResult(string Message, bool Sent = true, BillResponse? Bill = null);

/// <summary>
/// Everything the Bills tab and the Bill Details dialog do to a bill. Each method asks what has to
/// be asked, makes its calls and returns what to say; a call the server refuses comes back as a
/// result too, never as an exception. From the moment a method starts sending it reports through
/// <c>busy</c>: the screen shows that text and counts as busy until the method returns.
/// <para>The screen keeps what is its own: which bills are selected, the status line, the busy
/// flag and what to reload afterwards (<c>BillsViewModel.RunAsync</c>,
/// <c>BillDetailsViewModel.RunAsync</c>). Do not write the ask / send / report steps out again there.</para>
/// </summary>
/// <param name="adminTokens">Where the admin-only actions (change number, mark, delete) find the session, or ask for one.</param>
/// <param name="tallyReady">
/// Asked before anything goes to Tally; when it answers no it has told the operator why. The Bills
/// tab checks the Tally connection here. Null where there is no such check.
/// </param>
/// <param name="numbering">Where Change bill number reads the prefix, suffix and padding a number is formatted with.</param>
internal sealed class BillActions(
    IBillsApiClient api,
    IDialogService dialogs,
    AdminTokenStore? adminTokens = null,
    Func<CancellationToken, Task<bool>>? tallyReady = null,
    Func<NumberingSettingsDto?>? numbering = null)
{
    private readonly BillsAdminTokenGate _adminGate = new(adminTokens, dialogs);

    public async Task<BillActionResult?> PushAsync(BillRef bill, string? note, Action<string> busy, CancellationToken cancellationToken)
    {
        if (!await TallyReadyAsync(cancellationToken)) return null;

        return await SendAsync(
            $"Pushing {bill.Name}…",
            "Push failed",
            busy,
            async () => new BillActionResult(
                $"Pushed {bill.Name}.",
                Bill: await api.PushAsync(bill.Id, new PushBillRequest(note), cancellationToken)));
    }

    public async Task<BillActionResult?> PushSelectedAsync(IReadOnlyList<Guid> billIds, Action<string> busy, CancellationToken cancellationToken)
    {
        if (billIds.Count == 0) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;

        return await SendAsync(
            $"Pushing {billIds.Count} selected bill(s)…",
            "Push selected failed",
            busy,
            async () => new BillActionResult(BillsStatusFormatter.FormatBatchPushStatus(await api.PushSelectedAsync(
                new PushSelectedBillsRequest(billIds.ToArray(), "Selected push from Bills tab"),
                cancellationToken))));
    }

    public async Task<BillActionResult?> PushAllPendingAsync(Action<string> busy, CancellationToken cancellationToken)
    {
        var confirmed = await dialogs.ConfirmAsync(new ConfirmRequest(
            "Push all pending bills?",
            "Every pending and draft bill goes to Tally, oldest first. The run stops at the first failure.",
            "Push all"));
        if (!confirmed) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;

        return await SendAsync(
            "Pushing all pending bills (oldest first)…",
            "Push all pending failed",
            busy,
            async () =>
            {
                var response = await api.PushPendingAsync(
                    new PushPendingBillsRequest("Push all pending from Bills tab", null),
                    cancellationToken);
                return new BillActionResult(response.Matched == 0
                    ? "No pending bills to push."
                    : BillsStatusFormatter.FormatBatchPushStatus(response));
            });
    }

    public async Task<BillActionResult?> RetryAsync(IReadOnlyList<BillRef> bills, string? note, Action<string> busy, CancellationToken cancellationToken)
    {
        if (bills.Count == 0) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;

        return await ForEachAsync(
            bills,
            bill => bill.Name,
            "Retrying",
            "Retry",
            busy,
            bill => api.RetryAsync(bill.Id, new RetryBillPostingRequest(note), cancellationToken),
            count => count == 1 ? $"Retried {bills[0].Name}." : $"Retried {count} bill(s).");
    }

    public async Task<BillActionResult?> RepostAsync(IReadOnlyList<BillRef> bills, string? note, Action<string> busy, CancellationToken cancellationToken)
    {
        if (bills.Count == 0) return null;

        var what = bills.Count == 1 ? bills[0].Name : $"{bills.Count} bill(s)";
        if (!await dialogs.ConfirmAsync(BillConfirmations.Repost(what, bills.Any(bill => bill.IsPosted)))) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;

        return await ForEachAsync(
            bills,
            bill => bill.Name,
            "Reposting",
            "Repost",
            busy,
            bill => api.RepostAsync(bill.Id, new RepostBillRequest(Guid.NewGuid().ToString("N"), note), cancellationToken),
            count => count == 1 ? $"Reposted {bills[0].Name}." : $"Reposted {count} bill(s).");
    }

    public async Task<BillActionResult?> VoidAsync(BillRef bill, Action<string> busy, CancellationToken cancellationToken)
    {
        if (!await dialogs.ConfirmAsync(BillConfirmations.Void(bill.Name))) return null;

        return await SendAsync(
            "Voiding…",
            "Void failed",
            busy,
            async () => new BillActionResult(
                "Bill voided.",
                Bill: await api.VoidAsync(bill.Id, new VoidBillRequest(null), cancellationToken)));
    }

    /// <summary>The revision is a new bill, and the result carries it.</summary>
    public async Task<BillActionResult?> ReviseAsync(BillRef bill, Action<string> busy, CancellationToken cancellationToken)
        => await SendAsync(
            "Creating revision…",
            "Revise failed",
            busy,
            async () => new BillActionResult(
                "Revision created.",
                Bill: await api.ReviseAsync(bill.Id, new ReviseBillRequest(null), cancellationToken)));

    public async Task<BillActionResult?> ChangeNumberAsync(BillListRowViewModel row, Action<string> busy, CancellationToken cancellationToken)
    {
        var (token, stopped) = await AdminTokenAsync(cancellationToken);
        if (token is null) return stopped;

        var format = numbering?.Invoke();
        // A bill keeps the fiscal year of its bill date, so the preview uses
        // that rather than today's; they differ for a bill from an earlier year.
        var fiscalYear = row.BillDate is { } billDate
            ? InvoiceNumberFormatter.ComputeFiscalYear(billDate)
            : InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow);

        var (confirmed, newNumber, reason) = await dialogs.PromptChangeNumberAsync(new ChangeNumberPrompt(
            row.InvoiceNumber,
            format?.InvoicePrefix,
            format?.InvoiceSuffix,
            fiscalYear,
            format?.InvoicePadding ?? InvoiceNumberFormatter.DefaultPadding,
            $"{row.PartyName} · ₹{UiCulture.Rupees(row.GrandTotal)} · {row.StateChipLabel}"));
        if (!confirmed || string.IsNullOrWhiteSpace(newNumber)) return null;

        // A dry run first, so that what it warns of is put to the operator before anything changes.
        try
        {
            var dry = await api.ChangeInvoiceNumberAsync(
                row.Id,
                new ChangeBillNumberRequest(newNumber, reason, DryRun: true),
                token,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(dry.WarningSummary)
                && !await dialogs.ConfirmAsync(new ConfirmRequest(
                    "Change the number anyway?",
                    dry.WarningSummary,
                    "Change number",
                    IsWarning: true)))
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            return new BillActionResult($"Change number check failed: {ApiResponseReader.FormatError(ex)}", Sent: false);
        }

        return await SendAsync(
            $"Changing number of {row.InvoiceNumberDisplay} → {newNumber}…",
            "Change number failed",
            busy,
            async () =>
            {
                await api.ChangeInvoiceNumberAsync(
                    row.Id,
                    new ChangeBillNumberRequest(newNumber, reason, DryRun: false),
                    token,
                    cancellationToken);
                return new BillActionResult($"Invoice number changed to {newNumber}.");
            });
    }

    /// <summary>The two "mark" actions differ only in these words and in the call they make.</summary>
    private sealed record MarkAction(
        string Word,
        string Title,
        string Blocked,
        string Effect,
        string Check,
        Func<BillListRowViewModel, bool> Allowed,
        Func<IBillsApiClient, Guid, MarkBillStateRequest, string, CancellationToken, Task> Call);

    private static readonly MarkAction MarkPushed = new(
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

    public Task<BillActionResult?> MarkPushedAsync(IReadOnlyList<BillListRowViewModel> rows, Action<string> busy, CancellationToken cancellationToken)
        => MarkAsync(MarkPushed, rows, busy, cancellationToken);

    public Task<BillActionResult?> MarkPendingAsync(IReadOnlyList<BillListRowViewModel> rows, Action<string> busy, CancellationToken cancellationToken)
        => MarkAsync(MarkPending, rows, busy, cancellationToken);

    private async Task<BillActionResult?> MarkAsync(
        MarkAction mark,
        IReadOnlyList<BillListRowViewModel> rows,
        Action<string> busy,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return null;
        if (!rows.All(mark.Allowed)) return new BillActionResult(mark.Blocked, Sent: false);

        var (token, stopped) = await AdminTokenAsync(cancellationToken);
        if (token is null) return stopped;

        var subject = rows.Count == 1 ? rows[0].InvoiceNumberDisplay : $"{rows.Count} selected bills";
        var total = UiCulture.Rupees(rows.Sum(row => row.GrandTotal));
        var context = rows.Count == 1
            ? $"{subject} · {rows[0].PartyName} · ₹{total}"
            : $"{subject} · ₹{total}";
        var reason = await dialogs.PromptReasonAsync(
            new ReasonRequest(mark.Title, context, $"This marks {subject} {mark.Effect} {mark.Check}", mark.Title),
            cancellationToken);
        if (reason is null) return null;

        return await ForEachAsync(
            rows,
            row => row.InvoiceNumberDisplay,
            "Marking",
            $"Mark {mark.Word}",
            busy,
            row => mark.Call(api, row.Id, new MarkBillStateRequest(reason), token, cancellationToken),
            count => count == 1
                ? $"Marked {rows[0].InvoiceNumberDisplay} as {mark.Word}."
                : $"Marked {count} bill(s) as {mark.Word}.");
    }

    /// <summary>Deletes one bill, after a dry run that says whether Tally holds its voucher.</summary>
    public async Task<BillActionResult?> DeleteAsync(BillListRowViewModel row, Action<string> busy, CancellationToken cancellationToken)
    {
        var (token, stopped) = await AdminTokenAsync(cancellationToken);
        if (token is null) return stopped;

        DeleteBillResponse dry;
        try
        {
            dry = await api.DeleteAsync(row.Id, new DeleteBillRequest(null, DryRun: true), token, cancellationToken);
        }
        catch (Exception ex)
        {
            return new BillActionResult($"Delete check failed: {ApiResponseReader.FormatError(ex)}", Sent: false);
        }

        var question = dry.TallyDiverges
            ? $"{row.InvoiceNumberDisplay} was posted to Tally ({row.State}). Deleting it here does not remove it from Tally; reconcile it there by hand."
            : "This removes the bill from this database and frees its number if it was the latest. Its history stays in the audit log. Nothing is deleted in Tally.";
        if (!await dialogs.ConfirmAsync(new ConfirmRequest(
                $"Delete {row.InvoiceNumberDisplay}?", question, "Delete bill", IsDestructive: true, Note: "This cannot be undone."))) return null;

        return await SendAsync(
            $"Deleting {row.InvoiceNumberDisplay}…",
            "Delete failed",
            busy,
            async () =>
            {
                await api.DeleteAsync(row.Id, new DeleteBillRequest("Deleted from Bills tab", DryRun: false), token, cancellationToken);
                return new BillActionResult($"Deleted {row.InvoiceNumberDisplay}.");
            });
    }

    public async Task<BillActionResult?> DeleteSelectedAsync(IReadOnlyList<BillListRowViewModel> rows, Action<string> busy, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return null;

        var (token, stopped) = await AdminTokenAsync(cancellationToken);
        if (token is null) return stopped;

        var question = rows.Any(row => BillStateCapabilities.TallyDivergesIfDeleted(row.State))
            ? "Some of these bills were posted to Tally. Deleting them here does not remove them from Tally; reconcile those there by hand."
            : "This removes the bills from this database and frees their numbers where it can. Their history stays in the audit log. Nothing is deleted in Tally.";
        if (!await dialogs.ConfirmAsync(new ConfirmRequest(
                $"Delete {rows.Count} bill(s)?", question, "Delete bills", IsDestructive: true, Note: "This cannot be undone."))) return null;

        return await SendAsync(
            $"Deleting {rows.Count} bill(s)…",
            "Delete failed",
            busy,
            async () =>
            {
                var result = await api.DeleteSelectedAsync(
                    new DeleteSelectedBillsRequest(rows.Select(row => row.Id).ToArray(), "Batch delete from Bills tab"),
                    token,
                    cancellationToken);
                return new BillActionResult(result.Skipped == 0
                    ? $"Deleted {result.Deleted} bill(s)."
                    : $"Deleted {result.Deleted}; skipped {result.Skipped}. See details in the audit log.");
            });
    }

    private async Task<bool> TallyReadyAsync(CancellationToken cancellationToken)
        => tallyReady is null || await tallyReady(cancellationToken);

    /// <summary>The admin token, or why there is none: a result to show, or null when the operator just closed the unlock.</summary>
    private async Task<(string? Token, BillActionResult? Stopped)> AdminTokenAsync(CancellationToken cancellationToken)
    {
        var (token, message) = await _adminGate.EnsureAsync(cancellationToken);
        return (token, string.IsNullOrWhiteSpace(message) ? null : new BillActionResult(message, Sent: false));
    }

    /// <summary>
    /// The sending part of an action: <paramref name="starting"/> while it runs, then what it
    /// returns, or "<paramref name="failure"/>: reason" if the server refuses or cannot be reached.
    /// </summary>
    private static async Task<BillActionResult> SendAsync(
        string starting,
        string failure,
        Action<string> busy,
        Func<Task<BillActionResult>> send)
    {
        busy(starting);
        try
        {
            return await send();
        }
        catch (Exception ex)
        {
            return new BillActionResult($"{failure}: {ApiResponseReader.FormatError(ex)}");
        }
    }

    /// <summary>
    /// One call per bill, stopping at the first that fails. A single bill is named
    /// ("<paramref name="verb"/> SR/1…", "<paramref name="what"/> failed"); several are counted
    /// ("<paramref name="verb"/> 2 of 5…", "<paramref name="what"/> stopped").
    /// </summary>
    private static Task<BillActionResult> ForEachAsync<T>(
        IReadOnlyList<T> bills,
        Func<T, string> name,
        string verb,
        string what,
        Action<string> busy,
        Func<T, Task> send,
        Func<int, string> done)
    {
        string Progress(int index) => bills.Count == 1 ? $"{verb} {name(bills[0])}…" : $"{verb} {index + 1} of {bills.Count}…";

        return SendAsync(
            Progress(0),
            bills.Count == 1 ? $"{what} failed" : $"{what} stopped",
            busy,
            async () =>
            {
                for (var index = 0; index < bills.Count; index++)
                {
                    busy(Progress(index));
                    await send(bills[index]);
                }

                return new BillActionResult(done(bills.Count));
            });
    }
}

/// <summary>The questions the Bills tab and the Bill Details dialog both ask, worded once.</summary>
internal static class BillConfirmations
{
    /// <param name="what">What is being reposted, e.g. an invoice number or "3 bill(s)".</param>
    /// <param name="tallyMayHoldVoucher">True when a bill in the set is posted, so Tally may still hold its voucher.</param>
    public static ConfirmRequest Repost(string what, bool tallyMayHoldVoucher) => tallyMayHoldVoucher
        ? new ConfirmRequest(
            $"Repost {what}?",
            "A repost sends a new voucher to Tally. If Tally still holds the original voucher, this creates a duplicate sale.",
            "Repost to Tally",
            IsWarning: true,
            Note: "Repost only after the original voucher has been deleted in Tally.")
        : new ConfirmRequest($"Repost {what}?", "The bill goes to Tally again as a new voucher.", "Repost to Tally");

    public static ConfirmRequest Void(string what)
        => new(
            $"Void {what}?",
            "The bill is marked void. It can no longer be edited or sent to Tally.",
            "Void bill",
            IsDestructive: true,
            Note: "This cannot be undone.");
}
