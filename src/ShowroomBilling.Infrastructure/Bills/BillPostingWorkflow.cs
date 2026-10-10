using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillPostingWorkflow(
    ShowroomBillingDbContext dbContext,
    ITallyPoster tallyPoster,
    BillAuditStore auditStore,
    ILogger<BillPostingWorkflow> logger,
    ITallyCompanyHealthService? tallyCompanyHealthService = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private const string TallyAlterTargetTagName = "MASTER ID";

    // Once the bill is committed to `posting`, the outcome must be written
    // even if the HTTP caller disconnects. The bookkeeping before the Tally
    // call and the outcome save after it each get this budget instead of the
    // request token. They are timed separately: the Tally round-trip can take
    // as long as the budget itself, and must not eat into the outcome save.
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(30);

    internal Task<BillResponse> PushAsync(
        Guid billId,
        PushBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PushInternalAsync(
            billId,
            request.Reason,
            requireTallyPreflight: true,
            allowPostedSource: false,
            idempotencyKeyOverride: null,
            cancellationToken);
    }

    internal Task<BillBatchPushResponse> PushSelectedAsync(
        PushSelectedBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orderedIds = BillAdminSelection.NormalizeOrderedIds(request.BillIds);
        return PushBatchAsync(orderedIds, request.Reason, cancellationToken);
    }

    internal async Task<BillBatchPushResponse> PushPendingAsync(
        PushPendingBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxBills = Math.Clamp(request.MaxBills ?? 200, 1, 500);
        var showroomId = Showrooms.DefaultId;
        var targetIds = await dbContext.Bills
            .AsNoTracking()
            .Where(x => x.ShowroomId == showroomId && (x.State == BillStates.Pending || x.State == BillStates.Draft))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(maxBills)
            .ToListAsync(cancellationToken);

        return await PushBatchAsync(targetIds, request.Reason, cancellationToken);
    }

    internal async Task<BillPostingStatusResponse> RetryAsync(
        Guid billId,
        RetryBillPostingRequest request,
        CancellationToken cancellationToken = default)
    {
        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State != BillStates.Failed)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; retry is only allowed from 'failed'.");
        }
        await EnsureTallyReadyAsync(cancellationToken);
        await PushInternalAsync(
            billId,
            reason: request?.Reason ?? "retry",
            requireTallyPreflight: false,
            allowPostedSource: false,
            idempotencyKeyOverride: null,
            cancellationToken);
        return (await auditStore.GetPostingStatusAsync(billId, cancellationToken))!;
    }

    internal async Task<BillPostingStatusResponse> RepostAsync(
        Guid billId,
        RepostBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new RequestValidationException("Repost idempotency key is required.");
        }
        if (request.IdempotencyKey.Trim().Length > 128)
        {
            throw new RequestValidationException("Repost idempotency key must be 128 characters or fewer.");
        }

        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is not (BillStates.Posted or BillStates.Failed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; repost is only allowed from 'posted' or 'failed'.");
        }

        await EnsureTallyReadyAsync(cancellationToken);

        await PushInternalAsync(
            billId,
            reason: request.Reason ?? "repost",
            requireTallyPreflight: false,
            allowPostedSource: true,
            idempotencyKeyOverride: request.IdempotencyKey.Trim(),
            cancellationToken);
        return (await auditStore.GetPostingStatusAsync(billId, cancellationToken))!;
    }

    private async Task<BillResponse> PushInternalAsync(
        Guid billId,
        string? reason,
        bool requireTallyPreflight,
        bool allowPostedSource,
        string? idempotencyKeyOverride,
        CancellationToken cancellationToken)
    {
        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        var currentRevision = bill.CurrentRevisionId is Guid id
            ? await dbContext.BillRevisions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            : null;

        // Already-posted / mid-posting short-circuits.
        if (bill.State is BillStates.Posting)
        {
            return BillSerialization.MapResponse(bill, currentRevision);
        }

        // Allowed: pending / draft / failed (retry goes through here too), plus
        // posted only for the explicit repost path.
        var sourceStateAllowed = bill.State is BillStates.Pending or BillStates.Draft or BillStates.Failed
            || allowPostedSource && bill.State == BillStates.Posted;
        if (!sourceStateAllowed)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}' and cannot be pushed by this operation.");
        }

        if (currentRevision is null)
        {
            throw new BillStateConflictException($"Bill '{billId}' has no current revision to push.");
        }

        if (string.IsNullOrWhiteSpace(bill.InvoiceNumber) || string.IsNullOrWhiteSpace(bill.FiscalYear))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is missing a reserved invoice number; drafts are expected to reserve their serial at creation.");
        }

        if (requireTallyPreflight)
        {
            await EnsureTallyReadyAsync(cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var sourceState = bill.State;

        // Flip to `posting` atomically BEFORE calling Tally. Two concerns:
        //   1. Race: two concurrent push requests on the same bill must not both
        //      reach Tally. A conditional UPDATE gated on the pre-flip states
        //      ensures only one caller wins.
        //      The claim is also gated on the revision loaded above, so an edit
        //      that lands during Tally preflight cannot be overtaken by a push
        //      of the older revision.
        //   2. Crash recovery: `posting` must be visible in the DB before the
        //      Tally round-trip, so StuckPostingRecoveryHostedService can heal
        //      a stuck row if the API crashes mid-call. Do NOT wrap this flip
        //      and the Tally call in a single transaction.
        if (dbContext.UsesInMemoryProvider())
        {
            // ExecuteUpdateAsync is not reliably supported on the InMemory provider
            // used in unit tests, and those tests don't simulate real concurrency
            // (see CLAUDE.md "Test-harness reality"). Fall back to the original
            // mutate-then-save pattern for the in-memory code path only.
            bill.State = BillStates.Posting;
            bill.UpdatedAtUtc = now;
        }
        else
        {
            var claimedRevisionId = currentRevision.Id;
            var rowsAffected = await dbContext.Bills
                .Where(b => b.Id == billId
                    && b.CurrentRevisionId == claimedRevisionId
                    && (b.State == BillStates.Pending
                        || b.State == BillStates.Draft
                        || b.State == BillStates.Failed
                        || allowPostedSource && b.State == BillStates.Posted))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.State, BillStates.Posting)
                    .SetProperty(b => b.UpdatedAtUtc, now),
                    cancellationToken);

            if (rowsAffected == 0)
            {
                // Another concurrent push won the flip (or state drifted).
                // Reload and return whatever is current; caller sees the same
                // shape as the "already posting" short-circuit above.
                dbContext.ChangeTracker.Clear();
                var refreshed = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
                if (refreshed.CurrentRevisionId != claimedRevisionId
                    && refreshed.State is not BillStates.Posting)
                {
                    throw new BillStateConflictException(
                        $"Bill '{refreshed.InvoiceNumber ?? billId.ToString()}' was edited while the push was starting. Review the latest version and push again.");
                }
                var refreshedRevision = refreshed.CurrentRevisionId is Guid rid
                    ? await dbContext.BillRevisions.FirstOrDefaultAsync(x => x.Id == rid, cancellationToken)
                    : null;
                return BillSerialization.MapResponse(refreshed, refreshedRevision);
            }

            // Sync tracked entity snapshot with the row we just updated so the
            // final settle-state SaveChanges does not replay the posting flip.
            await dbContext.Entry(bill).ReloadAsync(CancellationToken.None);
        }

        using var claimCts = new CancellationTokenSource(SettleBudget, _timeProvider);
        var claimToken = claimCts.Token;

        currentRevision.SubmittedAtUtc ??= now;
        currentRevision.FinalizedAtUtc = now;

        var operation = bill.EditedAfterPush ? TallyPostOperation.Alter : TallyPostOperation.Create;
        var targetTagName = operation == TallyPostOperation.Alter ? TallyAlterTargetTagName : null;
        string? targetTagValue = null;
        if (bill.EditedAfterPush)
        {
            targetTagValue = await ResolveAlterTargetMasterIdAsync(bill.Id, claimToken);
        }

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? "manual-push" : reason.Trim();
        auditStore.Write(
            bill.Id,
            BillAuditEvents.PushRequested,
            bill.State,
            now,
            new
            {
                invoiceNumber = bill.InvoiceNumber,
                fiscalYear = bill.FiscalYear,
                reason = trimmedReason,
                tallyAction = operation.ToString()
            });
        await dbContext.SaveChangesAsync(claimToken);

        if (cancellationToken.IsCancellationRequested)
        {
            // The caller went away before anything was sent to Tally. Put the
            // bill back where it was rather than leaving it in `posting`.
            bill.State = sourceState;
            bill.UpdatedAtUtc = DateTimeOffset.UtcNow;
            auditStore.Write(
                bill.Id,
                BillAuditEvents.PushCancelled,
                bill.State,
                bill.UpdatedAtUtc,
                new { reason = "request cancelled before sending to Tally" });
            await dbContext.SaveChangesAsync(claimToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Call Tally synchronously. No queue, no retry.
        var payload = BillSerialization.DeserializePayload(currentRevision);
        var idempotencyKey = idempotencyKeyOverride ?? $"post:{bill.Id:N}:{currentRevision.Id:N}";
        TallyPostRequest? postRequest = null;

        TallyPostResponse tallyResult;
        if (operation == TallyPostOperation.Alter && targetTagValue is null)
        {
            tallyResult = TallyPostResponse.Failed(
                "TALLY_ALTER_TARGET_MISSING",
                "Edited bill cannot be pushed because the original Tally voucher master id is unavailable.");
        }
        else
        {
            postRequest = new TallyPostRequest(
                BillId: bill.Id,
                RevisionId: currentRevision.Id,
                RevisionNo: currentRevision.RevisionNo,
                BillType: bill.BillType,
                InvoiceNumber: bill.InvoiceNumber!,
                FiscalYear: bill.FiscalYear!,
                IdempotencyKey: idempotencyKey,
                Payload: payload,
                Operation: operation,
                TargetTagName: targetTagName,
                TargetTagValue: targetTagValue);

            try
            {
                tallyResult = await tallyPoster.PostAsync(postRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                // Once the poster has been invoked, an unexpected exception may have
                // happened after Tally accepted the voucher. Require reconciliation.
                tallyResult = TallyPostResponse.Unknown(
                    "TALLY_UNEXPECTED",
                    ex.Message);
            }
        }

        var settleAt = DateTimeOffset.UtcNow;
        if (tallyResult.Outcome == TallyPostOutcome.Posted)
        {
            bill.State = BillStates.Posted;
            if (operation == TallyPostOperation.Alter)
            {
                bill.EditedAfterPush = false;
            }
            bill.UpdatedAtUtc = settleAt;
            auditStore.Write(
                bill.Id,
                BillAuditEvents.TallyPosted,
                bill.State,
                settleAt,
                new
                {
                    remoteId = tallyResult.RemoteId,
                    tallyMasterId = tallyResult.TallyMasterId ?? targetTagValue,
                    tallyAction = operation.ToString(),
                    targetTagName,
                    targetTagValue,
                    invoiceNumber = bill.InvoiceNumber
                });
            logger.LogInformation(
                "Tally push succeeded. Invoice {InvoiceNumber} (bill {BillId}) accepted as remote {RemoteId}.",
                bill.InvoiceNumber, bill.Id, tallyResult.RemoteId);
        }
        else if (tallyResult.Outcome == TallyPostOutcome.Failed)
        {
            bill.State = BillStates.Failed;
            bill.UpdatedAtUtc = settleAt;
            auditStore.Write(
                bill.Id,
                BillAuditEvents.TallyFailed,
                bill.State,
                settleAt,
                new
                {
                    errorCode = tallyResult.ErrorCode,
                    errorMessage = tallyResult.ErrorMessage,
                    tallyAction = operation.ToString(),
                    targetTagName,
                    targetTagValue,
                    requestExcerpt = tallyResult.RequestExcerpt,
                    responseExcerpt = tallyResult.ResponseExcerpt
                });
            logger.LogWarning(
                "Tally push rejected. Invoice {InvoiceNumber} (bill {BillId}) failed: {ErrorCode} {ErrorMessage}. Response excerpt: {ResponseExcerpt}",
                bill.InvoiceNumber,
                bill.Id,
                tallyResult.ErrorCode,
                tallyResult.ErrorMessage,
                tallyResult.ResponseExcerpt);
        }
        else
        {
            bill.State = BillStates.ReconciliationRequired;
            bill.UpdatedAtUtc = settleAt;
            auditStore.Write(
                bill.Id,
                BillAuditEvents.TallyOutcomeUnknown,
                bill.State,
                settleAt,
                new
                {
                    errorCode = tallyResult.ErrorCode,
                    errorMessage = tallyResult.ErrorMessage,
                    idempotencyKey,
                    tallyAction = operation.ToString(),
                    targetTagName,
                    targetTagValue,
                    requestExcerpt = tallyResult.RequestExcerpt,
                    responseExcerpt = tallyResult.ResponseExcerpt
                });
            logger.LogWarning(
                "Tally outcome is unknown. Invoice {InvoiceNumber} (bill {BillId}) requires reconciliation: {ErrorCode} {ErrorMessage}",
                bill.InvoiceNumber,
                bill.Id,
                tallyResult.ErrorCode,
                tallyResult.ErrorMessage);
        }

        // Start the outcome budget only now that Tally has answered. A timer
        // started before the call would already have expired after a Tally
        // timeout, and the bill would stay in `posting` with no outcome.
        using var settleCts = new CancellationTokenSource(SettleBudget, _timeProvider);
        await dbContext.SaveChangesAsync(settleCts.Token);

        return BillSerialization.MapResponse(bill, currentRevision);
    }

    private async Task<BillBatchPushResponse> PushBatchAsync(
        IReadOnlyList<Guid> orderedBillIds,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (orderedBillIds.Count == 0)
        {
            return new BillBatchPushResponse(0, 0, 0, false, null, null, Array.Empty<BillBatchPushItemResult>());
        }

        await EnsureTallyReadyAsync(cancellationToken);

        var items = new List<BillBatchPushItemResult>(orderedBillIds.Count);
        var succeeded = 0;
        Guid? failedBillId = null;
        string? failureMessage = null;

        foreach (var billId in orderedBillIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop starting new pushes; any in-flight one has already settled.
                break;
            }

            try
            {
                var response = await PushInternalAsync(
                    billId,
                    reason,
                    requireTallyPreflight: false,
                    allowPostedSource: false,
                    idempotencyKeyOverride: null,
                    cancellationToken);
                if (response.State != BillStates.Posted)
                {
                    var status = await auditStore.GetPostingStatusAsync(billId, cancellationToken);
                    failedBillId = billId;
                    failureMessage = status?.LastErrorMessage
                        ?? (response.State == BillStates.Posting
                            ? "Another posting attempt is already in progress."
                            : $"Bill settled in '{response.State}' instead of 'posted'.");
                    items.Add(new BillBatchPushItemResult(
                        response.Id,
                        false,
                        response.State,
                        response.InvoiceNumber,
                        failureMessage));
                    break;
                }
                items.Add(new BillBatchPushItemResult(
                    response.Id,
                    true,
                    response.State,
                    response.InvoiceNumber,
                    null));
                succeeded++;
            }
            catch (Exception ex)
            {
                dbContext.ChangeTracker.Clear();
                failedBillId = billId;
                failureMessage = ex.Message;
                items.Add(new BillBatchPushItemResult(
                    billId,
                    false,
                    null,
                    null,
                    ex.Message));
                break;
            }
        }

        return new BillBatchPushResponse(
            orderedBillIds.Count,
            succeeded,
            failedBillId is null ? 0 : 1,
            failedBillId is not null && succeeded < orderedBillIds.Count,
            failedBillId,
            failureMessage,
            items);
    }

    private async Task EnsureTallyReadyAsync(CancellationToken cancellationToken)
    {
        if (tallyCompanyHealthService is null)
        {
            return;
        }

        TallyCompanyHealthResponse health;
        try
        {
            health = await tallyCompanyHealthService.CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TallyPreflightUnavailableException(
                $"Tally push blocked: Tally health could not be checked. {ex.Message}");
        }

        if (IsTallyReady(health))
        {
            return;
        }

        throw new TallyPreflightUnavailableException(BuildTallyPreflightMessage(health));
    }

    private static bool IsTallyReady(TallyCompanyHealthResponse health) =>
        string.Equals(health.Status, TallyHealthStatuses.Healthy, StringComparison.OrdinalIgnoreCase)
        && health.TallyReachable
        && health.ActiveCompanyOpen;

    private static string BuildTallyPreflightMessage(TallyCompanyHealthResponse health)
    {
        var message = string.IsNullOrWhiteSpace(health.Message)
            ? "Tally is not ready for posting."
            : health.Message.Trim();
        return $"Tally push blocked: {message}";
    }

    private async Task<string?> ResolveAlterTargetMasterIdAsync(Guid billId, CancellationToken cancellationToken)
    {
        var billIdText = billId.ToString();
        var lastReopen = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && e.EventType == BillAuditEvents.EditReopened)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastReopen is null)
        {
            return null;
        }

        var lastPreEditSuccess = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && e.EventType == BillAuditEvents.TallyPosted
                && e.CreatedAtUtc < lastReopen.CreatedAtUtc)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastPreEditSuccess is null)
        {
            return null;
        }

        return TallyIds.NormalizePositiveInteger(BillAuditStore.ReadDetailsString(lastPreEditSuccess.PayloadJson, "tallyMasterId"))
            ?? TallyIds.NormalizePositiveInteger(BillAuditStore.ReadDetailsString(lastPreEditSuccess.PayloadJson, "remoteId"));
    }





}
