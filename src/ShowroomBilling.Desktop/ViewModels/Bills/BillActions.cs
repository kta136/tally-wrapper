using System.Globalization;
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
/// <param name="Tone">
/// How the message is to be read, as a <see cref="BillStateDisplay"/> tone: "ok" for done, "err" for
/// refused or failed, "warn" for stopped before it began. The Bills tab colours its status dot with it.
/// </param>
internal sealed record BillActionResult(
    string Message,
    bool Sent = true,
    BillResponse? Bill = null,
    string Tone = BillStateDisplay.ToneOk)
{
    /// <summary>The action did not go through: the server refused it, or could not be reached.</summary>
    public static BillActionResult Failed(string message, bool sent = true) => new(message, sent, Tone: BillStateDisplay.ToneErr);

    /// <summary>The action stopped before it sent anything, for a reason the operator can do something about.</summary>
    public static BillActionResult Stopped(string message) => new(message, Sent: false, Tone: BillStateDisplay.ToneWarn);
}

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
        if (await InPushOrderAsync([bill.Id], cancellationToken) is null) return null;

        return await SendAsync(
            $"Pushing {bill.Name}…",
            "Push failed",
            busy,
            async () =>
            {
                var pushed = await api.PushAsync(bill.Id, new PushBillRequest(note), cancellationToken);
                return FiledAs(
                    Outcome("Pushed", bill.Name, pushed.State),
                    pushed.InvoiceNumber,
                    pushed.TallyVoucherNumber,
                    // Tally check compares the current year only.
                    inTallyCheck: pushed.FiscalYear is null
                        || pushed.FiscalYear == InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow)) with { Bill = pushed };
            });
    }

    public async Task<BillActionResult?> PushSelectedAsync(IReadOnlyList<Guid> billIds, Action<string> busy, CancellationToken cancellationToken)
    {
        if (billIds.Count == 0) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;
        if (await InPushOrderAsync(billIds, cancellationToken) is not { } ordered) return null;

        return await SendAsync(
            $"Pushing {billIds.Count} selected bill(s)…",
            "Push selected failed",
            busy,
            async () => BatchPushOutcome(await api.PushSelectedAsync(
                new PushSelectedBillsRequest(ordered.ToArray(), "Selected push from Bills tab"),
                cancellationToken)));
    }

    /// <summary>
    /// What a push of several bills in one call came to. The run stops at the first bill Tally
    /// does not take, and that bill is said as a push of it alone is said, by its number, so the
    /// toolbar and the row menu do not word one outcome two ways.
    /// </summary>
    private static BillActionResult BatchPushOutcome(BillBatchPushResponse response)
    {
        var stoppedAt = response.Failed > 0 ? response.Items.LastOrDefault(item => !item.Succeeded) : null;
        if (stoppedAt is null)
        {
            var only = response is { Matched: 1, Succeeded: 1 } ? response.Items.FirstOrDefault() : null;
            // A batch answer does not say which year its bills are of, and Tally check shows
            // the current one only, so it is not named here; the row says what Tally holds.
            if (only is { InvoiceNumber: { } onlyNumber })
            {
                return FiledAs(new BillActionResult($"Pushed {onlyNumber}."), onlyNumber, only.TallyVoucherNumber, inTallyCheck: false);
            }

            var filedElsewhere = response.Items.Count(item => item.Succeeded && NumberedOtherwise(item.InvoiceNumber, item.TallyVoucherNumber));
            var result = new BillActionResult(
                BillsStatusFormatter.FormatBatchPushStatus(response),
                Tone: response.Failed > 0 ? BillStateDisplay.ToneErr
                    : response.Cancelled || response.Succeeded == 0 ? BillStateDisplay.ToneWarn
                    : BillStateDisplay.ToneOk);
            return filedElsewhere == 0 || result.Tone != BillStateDisplay.ToneOk
                ? result
                : result with
                {
                    Message = $"{result.Message} Tally filed {filedElsewhere} under another number: their rows say which.",
                    Tone = BillStateDisplay.ToneWarn,
                };
        }

        var name = stoppedAt.InvoiceNumber ?? "a bill";
        var why = stoppedAt.BillState is BillStates.Failed or BillStates.ReconciliationRequired
            ? Outcome("Pushed", name, stoppedAt.BillState, stoppedAt.ErrorMessage).Message
            : $"{name} could not be pushed: {stoppedAt.ErrorMessage ?? response.FailureMessage}";
        return BillActionResult.Failed(response.Succeeded == 0
            ? why
            : $"Pushed {response.Succeeded} bill(s), then stopped. {why}");
    }

    public async Task<BillActionResult?> PushAllPendingAsync(Action<string> busy, CancellationToken cancellationToken)
    {
        var confirmed = await dialogs.ConfirmAsync(new ConfirmRequest(
            "Push all pending bills?",
            "Every pending bill goes to Tally, by date and then number. The run stops at the first failure.",
            "Push all"));
        if (!confirmed) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;

        return await SendAsync(
            "Pushing all pending bills (by date and number)…",
            "Push all pending failed",
            busy,
            async () =>
            {
                var response = await api.PushPendingAsync(
                    new PushPendingBillsRequest("Push all pending from Bills tab", null),
                    cancellationToken);
                return response.Matched == 0
                    ? new BillActionResult("No pending bills to push.", Tone: BillStateDisplay.ToneWarn)
                    : BatchPushOutcome(response);
            });
    }

    public async Task<BillActionResult?> RetryAsync(IReadOnlyList<BillRef> bills, string? note, Action<string> busy, CancellationToken cancellationToken)
    {
        if (bills.Count == 0) return null;
        if (!await TallyReadyAsync(cancellationToken)) return null;
        if (await InPushOrderAsync(bills.Select(bill => bill.Id).ToArray(), cancellationToken) is not { } ordered) return null;
        var byId = bills.ToDictionary(bill => bill.Id);
        var inOrder = ordered.Select(id => byId[id]).ToArray();

        // A bill Tally refuses stays out, so the next one would take its number: stop there, as
        // a push of ticked bills does.
        return await ForEachAsync(
            inOrder,
            bill => bill.Name,
            "Retrying",
            "Retry",
            busy,
            bill => api.RetryAsync(bill.Id, new RetryBillPostingRequest(note), cancellationToken),
            sent => PostingOutcome("Retried", inOrder, sent),
            stopAfter: status => status.BillState != BillStates.Posted);
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
            sent => PostingOutcome("Reposted", bills, sent));
    }

    /// <summary>
    /// What a push, retry or repost came to, read from the state the server left the bill in. The
    /// server answers such a call with the bill also when Tally refused the voucher or never
    /// answered, so that the call returned does not mean the bill is in Tally.
    /// </summary>
    private static BillActionResult Outcome(string done, string name, string? state, string? reason = null) => state switch
    {
        BillStates.Posted => new BillActionResult($"{done} {name}."),
        BillStates.Failed => BillActionResult.Failed(string.IsNullOrWhiteSpace(reason)
            ? $"Tally did not accept {name}. Its timeline has the reason."
            : $"Tally did not accept {name}: {reason}"),
        BillStates.ReconciliationRequired => BillActionResult.Failed(
            $"Tally did not answer for {name}. Check its voucher in Tally before anything else."),
        _ => new BillActionResult(
            $"{name} is now {BillStateDisplay.Label(state, editedAfterPush: false).ToLowerInvariant()}.",
            Tone: BillStateDisplay.ToneWarn),
    };

    /// <summary>
    /// A push Tally took and filed under a number of its own (Automatic numbering ignores the number
    /// sent) is said so, with where to look, instead of a plain "Pushed".
    /// </summary>
    private static BillActionResult FiledAs(BillActionResult result, string? invoiceNumber, string? tallyNumber, bool inTallyCheck = true) =>
        result.Tone == BillStateDisplay.ToneOk && NumberedOtherwise(invoiceNumber, tallyNumber)
            ? result with
            {
                Message = inTallyCheck
                    ? $"{result.Message} Tally filed it as {tallyNumber}: see Settings → Tally check."
                    : $"{result.Message} Tally filed it as {tallyNumber}.",
                Tone = BillStateDisplay.ToneWarn,
            }
            : result;

    private static bool NumberedOtherwise(string? invoiceNumber, string? tallyNumber) =>
        !string.IsNullOrWhiteSpace(tallyNumber) && !string.Equals(tallyNumber, invoiceNumber, StringComparison.Ordinal);

    private static BillActionResult PostingOutcome(string done, IReadOnlyList<BillRef> bills, IReadOnlyList<BillPostingStatusResponse> sent)
    {
        if (bills.Count == 1)
        {
            return Outcome(done, bills[0].Name, sent[0].BillState, sent[0].LastErrorMessage);
        }

        var posted = sent.Count(status => status.BillState == BillStates.Posted);
        if (sent.Count < bills.Count)
        {
            return BillActionResult.Failed(
                $"{done} {sent.Count} of {bills.Count} bill(s), then stopped: {bills[sent.Count - 1].Name} did not go to Tally. The list shows why.");
        }

        return posted == sent.Count
            ? new BillActionResult($"{done} {sent.Count} bill(s).")
            : BillActionResult.Failed($"{done} {sent.Count} bill(s): {posted} went to Tally, {sent.Count - posted} did not. The list shows which.");
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
            return BillActionResult.Failed($"Change number check failed: {ApiResponseReader.FormatError(ex)}", sent: false);
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
        Func<IBillsApiClient, Guid, MarkBillStateRequest, string, CancellationToken, Task<BillResponse>> Call);

    private static readonly MarkAction MarkPushed = new(
        "pushed",
        "Mark as pushed",
        "Mark pushed blocked: every selected bill must be pending or failed.",
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
        if (!rows.All(mark.Allowed)) return BillActionResult.Stopped(mark.Blocked);

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
            marked => new BillActionResult(marked.Count == 1
                ? $"Marked {rows[0].InvoiceNumberDisplay} as {mark.Word}."
                : $"Marked {marked.Count} bill(s) as {mark.Word}."));
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
            return BillActionResult.Failed($"Delete check failed: {ApiResponseReader.FormatError(ex)}", sent: false);
        }

        var question = dry.TallyDiverges
            ? $"{row.InvoiceNumberDisplay} was posted to Tally ({row.StateChipLabel}). Deleting it here does not remove it from Tally; reconcile it there by hand."
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
                return result.Skipped == 0
                    ? new BillActionResult($"Deleted {result.Deleted} bill(s).")
                    : new BillActionResult(
                        $"Deleted {result.Deleted}; skipped {result.Skipped}. See details in the audit log.",
                        Tone: BillStateDisplay.ToneWarn);
            });
    }

    private async Task<bool> TallyReadyAsync(CancellationToken cancellationToken)
        => tallyReady is null || await tallyReady(cancellationToken);

    /// <summary>
    /// The bills to push or retry, in the order they should reach Tally, or null when the operator
    /// chose not to go ahead. Tally numbers vouchers by date and, within a day, in the order they
    /// arrive, so a bill pushed while an earlier-numbered bill of its day (or of an earlier day) is
    /// not in Tally yet is filed under that bill's number: the server names such bills and gives the
    /// order (by date, then number, as the bills are now), and the operator is asked before going
    /// ahead. A server that cannot answer (an older one, or none) leaves the bills as given, and the
    /// push itself then says what is wrong.
    /// </summary>
    private async Task<IReadOnlyList<Guid>?> InPushOrderAsync(IReadOnlyList<Guid> billIds, CancellationToken cancellationToken)
    {
        BillPushOrderResponse? order;
        try
        {
            order = await api.GetPushOrderAsync(new BillPushOrderRequest(billIds), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return billIds;
        }

        // Only an order of exactly these bills is used.
        var ordered = order?.PushOrder is { } given
            && given.Count == billIds.Count
            && given.ToHashSet().SetEquals(billIds)
                ? given
                : billIds;
        var earlier = order?.EarlierBills ?? [];
        return earlier.Count == 0 || await dialogs.ConfirmAsync(BillConfirmations.PushOrder(earlier))
            ? ordered
            : null;
    }

    /// <summary>The admin token, or why there is none: a result to show, or null when the operator just closed the unlock.</summary>
    private async Task<(string? Token, BillActionResult? Stopped)> AdminTokenAsync(CancellationToken cancellationToken)
    {
        var (token, message) = await _adminGate.EnsureAsync(cancellationToken);
        return (token, string.IsNullOrWhiteSpace(message) ? null : BillActionResult.Stopped(message));
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
            return BillActionResult.Failed($"{failure}: {ApiResponseReader.FormatError(ex)}");
        }
    }

    /// <summary>
    /// One call per bill, stopping at the first that fails. A single bill is named
    /// ("<paramref name="verb"/> SR/1…", "<paramref name="what"/> failed"); several are counted
    /// ("<paramref name="verb"/> 2 of 5…", "<paramref name="what"/> stopped").
    /// </summary>
    /// <param name="stopAfter">When it says so for a bill's answer, the bills after it are not sent.</param>
    private static Task<BillActionResult> ForEachAsync<T, TSent>(
        IReadOnlyList<T> bills,
        Func<T, string> name,
        string verb,
        string what,
        Action<string> busy,
        Func<T, Task<TSent>> send,
        Func<IReadOnlyList<TSent>, BillActionResult> done,
        Func<TSent, bool>? stopAfter = null)
    {
        string Progress(int index) => bills.Count == 1 ? $"{verb} {name(bills[0])}…" : $"{verb} {index + 1} of {bills.Count}…";

        return SendAsync(
            Progress(0),
            bills.Count == 1 ? $"{what} failed" : $"{what} stopped",
            busy,
            async () =>
            {
                var sent = new List<TSent>(bills.Count);
                for (var index = 0; index < bills.Count; index++)
                {
                    busy(Progress(index));
                    var answer = await send(bills[index]);
                    sent.Add(answer);
                    if (stopAfter?.Invoke(answer) == true)
                    {
                        break;
                    }
                }

                return done(sent);
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

    /// <summary>Asked before a push or retry that would go past bills that should reach Tally first.</summary>
    public static ConfirmRequest PushOrder(IReadOnlyList<UnpushedEarlierBill> earlier)
    {
        const int shown = 5;
        var facts = earlier
            .Take(shown)
            .Select(b => new ConfirmFact(
                b.InvoiceNumber,
                $"{(b.BillDate is { } date ? date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : "no date")} · {BillStateDisplay.Label(b.State, editedAfterPush: false)}"))
            .ToList();
        if (earlier.Count > shown)
        {
            facts.Add(new ConfirmFact("And", $"{earlier.Count - shown} more"));
        }

        return new ConfirmRequest(
            earlier.Count == 1 ? $"Push before {earlier[0].InvoiceNumber}?" : $"Push before {earlier.Count} earlier bills?",
            earlier.Count == 1
                ? $"{earlier[0].InvoiceNumber} has an earlier number and is not in Tally yet. Tally numbers a day's vouchers in the order they arrive, so pushed now, this bill would be filed under an earlier number than its own."
                : "These bills have earlier numbers and are not in Tally yet. Tally numbers a day's vouchers in the order they arrive, so pushed now, this would be filed under an earlier number than its own.",
            "Push anyway",
            IsWarning: true,
            Note: "Push or retry the earlier bills first.",
            Facts: facts);
    }

    public static ConfirmRequest Void(string what)
        => new(
            $"Void {what}?",
            "The bill is marked void. It can no longer be edited or sent to Tally.",
            "Void bill",
            IsDestructive: true,
            Note: "This cannot be undone.");
}
