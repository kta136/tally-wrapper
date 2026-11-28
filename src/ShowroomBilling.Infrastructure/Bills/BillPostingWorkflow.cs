using System.Globalization;
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

public sealed class BillPostingWorkflow(
    ShowroomBillingDbContext dbContext,
    ITallyPoster tallyPoster,
    BillAuditStore auditStore,
    ILogger<BillPostingWorkflow> logger,
    ITallyCompanyHealthService? tallyCompanyHealthService = null,
    TimeProvider? timeProvider = null,
    ITallyVoucherLookup? tallyVoucherLookup = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    private const string TallyAlterTargetTagName = "MASTER ID";

    // Once the bill is committed to `posting`, the outcome must be written
    // even if the HTTP caller disconnects. The bookkeeping before the Tally
    // call and the outcome save after it each get this budget instead of the
    // request token. They are timed separately: the Tally round-trip can take
    // as long as the budget itself, and must not eat into the outcome save.
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(30);

    // The read-back after an accepted voucher. Tally has just answered, so it
    // is quick (0.1-0.2 s measured); the budget only stops a hung read from
    // holding up the answer to the operator.
    private static readonly TimeSpan ReadBackBudget = TimeSpan.FromSeconds(10);

    public Task<BillResponse> PushAsync(
        Guid billId,
        PushBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PushInternalAsync(
            billId,
            request.Reason,
            requireTallyPreflight: true,
            repostKey: null,
            cancellationToken);
    }

    public Task<BillBatchPushResponse> PushSelectedAsync(
        PushSelectedBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orderedIds = BillAdminSelection.NormalizeOrderedIds(request.BillIds);
        return PushBatchAsync(orderedIds, request.Reason, cancellationToken);
    }

    public async Task<BillBatchPushResponse> PushPendingAsync(
        PushPendingBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxBills = Math.Clamp(request.MaxBills ?? 200, 1, 500);
        // By date, then number: Tally numbers a day's vouchers in the order they arrive, and a
        // bill renumbered since it was saved no longer sits where its creation time puts it.
        var targetIds = await dbContext.Bills
            .AsNoTracking()
            .Where(x => x.State == BillStates.Pending)
            .Select(x => new
            {
                x.Id,
                x.CreatedAtUtc,
                x.InvoiceCore,
                BillDate = dbContext.BillRevisions.Where(r => r.Id == x.CurrentRevisionId).Select(r => r.BillDate).FirstOrDefault(),
            })
            .OrderBy(x => x.BillDate == null)
            .ThenBy(x => x.BillDate)
            .ThenBy(x => x.InvoiceCore == null)
            .ThenBy(x => x.InvoiceCore)
            .ThenBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(maxBills)
            .ToListAsync(cancellationToken);

        return await PushBatchAsync(targetIds, request.Reason, cancellationToken);
    }

    public async Task<BillPostingStatusResponse> RetryAsync(
        Guid billId,
        RetryBillPostingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        if (!BillStateCapabilities.CanRetry(bill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; retry is only allowed from 'failed'.");
        }
        await EnsureTallyReadyAsync(cancellationToken);
        await PushInternalAsync(
            billId,
            reason: request.Reason ?? "retry",
            requireTallyPreflight: false,
            repostKey: null,
            cancellationToken);
        return (await auditStore.GetPostingStatusAsync(billId, cancellationToken))!;
    }

    public async Task<BillPostingStatusResponse> RepostAsync(
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
        if (!BillStateCapabilities.CanRepost(bill.State))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; repost is only allowed from 'posted' or 'failed'.");
        }

        await EnsureTallyReadyAsync(cancellationToken);

        await PushInternalAsync(
            billId,
            reason: request.Reason ?? "repost",
            requireTallyPreflight: false,
            repostKey: request.IdempotencyKey.Trim(),
            cancellationToken);
        return (await auditStore.GetPostingStatusAsync(billId, cancellationToken))!;
    }

    private async Task<BillResponse> PushInternalAsync(
        Guid billId,
        string? reason,
        bool requireTallyPreflight,
        string? repostKey,
        CancellationToken cancellationToken)
    {
        var bill = await dbContext.LoadTrackedBillAsync(billId, cancellationToken);
        var currentRevision = bill.CurrentRevisionId is Guid id
            ? await dbContext.BillRevisions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            : null;

        if (bill.State is BillStates.Posting)
        {
            return BillSerialization.MapResponse(bill, currentRevision);
        }

        string[] sourceStates = repostKey is null
            ? [.. BillStateCapabilities.PushOrRetryStates]
            : [.. BillStateCapabilities.PushOrRetryStates, BillStates.Posted];
        if (!sourceStates.Contains(bill.State))
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

        // Resolve the target before claiming. A caller can still cancel a slow lookup without changing the bill.
        uint? alterVersion = bill.EditedAfterPush ? bill.RowVersion : null;
        var alterTarget = alterVersion.HasValue
            ? await FindAlterTargetAsync(bill, cancellationToken)
            : default;

        var claimedAt = DateTimeOffset.UtcNow;
        var sourceState = bill.State;
        var concurrentBill = await ClaimForPostingAsync(
            bill, currentRevision.Id, alterVersion, sourceStates, claimedAt, cancellationToken);
        if (concurrentBill is not null)
        {
            return concurrentBill;
        }

        var operation = bill.EditedAfterPush ? TallyPostOperation.Alter : TallyPostOperation.Create;
        await RecordPushRequestAsync(bill, sourceState, operation, alterTarget, reason, claimedAt, cancellationToken);

        var request = new TallyPostRequest(
            BillId: bill.Id,
            InvoiceNumber: bill.InvoiceNumber!,
            IdempotencyKey: repostKey ?? $"post:{bill.Id:N}:{currentRevision.Id:N}",
            Payload: BillSerialization.DeserializePayload(currentRevision),
            Operation: operation,
            TargetTagName: operation == TallyPostOperation.Alter ? TallyAlterTargetTagName : null,
            TargetTagValue: operation == TallyPostOperation.Alter ? alterTarget.MasterId : null,
            TargetVoucherDate: alterTarget.VoucherDate,
            TargetCompany: alterTarget.Destination?.Company,
            TargetVoucherType: alterTarget.Destination?.VoucherType);
        var tallyResult = await PostToTallyAsync(request, alterTarget.Failure, cancellationToken);
        var postedRow = await SavePushOutcomeAsync(bill, request, tallyResult);

        var filedAs = postedRow is null ? null : await RecordVoucherAsFiledAsync(
            bill, tallyResult.TallyMasterId ?? request.TargetTagValue, tallyResult.Company, postedRow.Id);

        return BillSerialization.MapResponse(bill, currentRevision) with { TallyVoucherNumber = filedAs };
    }

    /// <returns>The current bill if another operation won the claim, or null when this caller owns it.</returns>
    private async Task<BillResponse?> ClaimForPostingAsync(
        BillEntity bill,
        Guid claimedRevisionId,
        uint? alterVersion,
        string[] sourceStates,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken)
    {
        // Commit the claim before contacting Tally so concurrent pushes see it and crash recovery can find it.
        // A changed revision invalidates any push. On an Alter, a rename or admin mark can also
        // invalidate the resolved voucher without creating a revision, so check xmin as well.
        var rowsAffected = await dbContext.Bills
            .Where(b => b.Id == bill.Id
                && b.CurrentRevisionId == claimedRevisionId
                && (alterVersion == null || b.RowVersion == alterVersion)
                && sourceStates.Contains(b.State))
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BillStates.Posting)
                .SetProperty(b => b.UpdatedAtUtc, claimedAt),
                cancellationToken);

        if (rowsAffected == 0)
        {
            // Another concurrent push won the flip (or state drifted).
            // Reload and return whatever is current; caller sees the same
            // shape as the "already posting" short-circuit above.
            dbContext.ChangeTracker.Clear();
            var refreshed = await dbContext.LoadTrackedBillAsync(bill.Id, cancellationToken);
            var staleAlter = alterVersion.HasValue && refreshed.State == bill.State;
            if ((refreshed.CurrentRevisionId != claimedRevisionId || staleAlter) && refreshed.State != BillStates.Posting)
            {
                throw new BillStateConflictException(
                    $"Bill '{refreshed.InvoiceNumber ?? bill.Id.ToString()}' changed while the push was starting. Review the latest version and push again.");
            }
            var refreshedRevision = refreshed.CurrentRevisionId is Guid rid
                ? await dbContext.BillRevisions.FirstOrDefaultAsync(x => x.Id == rid, cancellationToken)
                : null;
            return BillSerialization.MapResponse(refreshed, refreshedRevision);
        }

        // Sync tracked entity snapshot with the row we just updated so the
        // final settle-state SaveChanges does not replay the posting flip.
        await dbContext.Entry(bill).ReloadAsync(CancellationToken.None);
        return null;
    }

    private async Task RecordPushRequestAsync(
        BillEntity bill,
        string sourceState,
        TallyPostOperation operation,
        AlterTarget alterTarget,
        string? reason,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken)
    {
        using var claimCts = new CancellationTokenSource(SettleBudget, _timeProvider);

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? "manual-push" : reason.Trim();
        auditStore.Write(
            bill.Id,
            BillAuditEvents.PushRequested,
            bill.State,
            claimedAt,
            new
            {
                invoiceNumber = bill.InvoiceNumber,
                fiscalYear = bill.FiscalYear,
                reason = trimmedReason,
                tallyAction = operation.ToString(),
                // The voucher an Alter names; none for a Create.
                targetTagValue = operation == TallyPostOperation.Alter ? alterTarget.MasterId : null,
                // Set when the voucher to alter was found by asking Tally for this number.
                voucherLookedUpAs = alterTarget.LookedUpNumber,
                // The date the voucher to alter had in Tally, which the Alter names it by.
                voucherDateInTally = alterTarget.VoucherDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            });
        await dbContext.SaveChangesAsync(claimCts.Token);

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
            await dbContext.SaveChangesAsync(claimCts.Token);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task<TallyPostResponse> PostToTallyAsync(
        TallyPostRequest request,
        TallyPostResponse? targetFailure,
        CancellationToken cancellationToken)
    {
        if (request.Operation == TallyPostOperation.Alter && (request.TargetTagValue is null || request.TargetVoucherDate is null))
        {
            return targetFailure ?? TallyPostResponse.Failed(
                "TALLY_ALTER_TARGET_MISSING",
                "Edited bill cannot be pushed because the original Tally voucher master id is unavailable.");
        }

        try
        {
            return await tallyPoster.PostAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // Tally may already have accepted the voucher. Require reconciliation.
            return TallyPostResponse.Unknown("TALLY_UNEXPECTED", ex.Message);
        }
    }

    private async Task<AuditEventEntity?> SavePushOutcomeAsync(
        BillEntity bill,
        TallyPostRequest request,
        TallyPostResponse tallyResult)
    {
        bill.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AuditEventEntity? postedRow = null;
        if (tallyResult.Outcome == TallyPostOutcome.Posted)
        {
            bill.State = BillStates.Posted;
            if (request.Operation == TallyPostOperation.Alter)
            {
                bill.EditedAfterPush = false;
            }
            postedRow = auditStore.Write(
                bill.Id,
                BillAuditEvents.TallyPosted,
                bill.State,
                bill.UpdatedAtUtc,
                new
                {
                    remoteId = tallyResult.RemoteId,
                    tallyMasterId = tallyResult.TallyMasterId ?? request.TargetTagValue,
                    tallyAction = request.Operation.ToString(),
                    request.TargetTagName,
                    request.TargetTagValue,
                    invoiceNumber = bill.InvoiceNumber,
                    // The id above names a voucher only within this company.
                    company = tallyResult.Company,
                    // Tally already had a voucher with this REMOTEID and changed it.
                    updatedExistingVoucher = tallyResult.UpdatedExistingVoucher
                });
            logger.LogInformation(
                "Tally push succeeded. Invoice {InvoiceNumber} (bill {BillId}) accepted as remote {RemoteId}{Existing}.",
                bill.InvoiceNumber,
                bill.Id,
                tallyResult.RemoteId,
                tallyResult.UpdatedExistingVoucher ? ", updating the voucher Tally already had for it" : string.Empty);
        }
        else if (tallyResult.Outcome == TallyPostOutcome.Failed)
        {
            bill.State = BillStates.Failed;
            auditStore.Write(
                bill.Id,
                BillAuditEvents.TallyFailed,
                bill.State,
                bill.UpdatedAtUtc,
                new
                {
                    errorCode = tallyResult.ErrorCode,
                    errorMessage = tallyResult.ErrorMessage,
                    tallyAction = request.Operation.ToString(),
                    request.TargetTagName,
                    request.TargetTagValue,
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
            auditStore.Write(
                bill.Id,
                BillAuditEvents.TallyOutcomeUnknown,
                bill.State,
                bill.UpdatedAtUtc,
                new
                {
                    errorCode = tallyResult.ErrorCode,
                    errorMessage = tallyResult.ErrorMessage,
                    request.IdempotencyKey,
                    tallyAction = request.Operation.ToString(),
                    request.TargetTagName,
                    request.TargetTagValue,
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
        return postedRow;
    }

    /// <summary>
    /// Reads a voucher Tally has just accepted back by its id and records the
    /// number, date and total Tally filed it under (<c>tally.voucher.filed</c>).
    /// The push stands whatever happens here: a read that fails or finds
    /// nothing is logged and the number is left unknown. A master id names a
    /// voucher only within its company, so the read is used only when it was
    /// made in the company the push went to.
    /// </summary>
    private async Task<string?> RecordVoucherAsFiledAsync(BillEntity bill, string? tallyMasterId, string? pushedToCompany, Guid pushAuditId)
    {
        if (tallyVoucherLookup is null || TallyIds.NormalizePositiveInteger(tallyMasterId) is not { } masterId)
        {
            return null;
        }

        TallyVoucherRef? voucher;
        try
        {
            using var readCts = new CancellationTokenSource(ReadBackBudget, _timeProvider);
            voucher = await tallyVoucherLookup.FindByMasterIdAsync(masterId, bill.FiscalYear!, readCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Invoice {InvoiceNumber} (bill {BillId}) was pushed, but its voucher {MasterId} could not be read back from Tally.",
                bill.InvoiceNumber, bill.Id, masterId);
            return null;
        }

        if (voucher is null)
        {
            logger.LogWarning(
                "Invoice {InvoiceNumber} (bill {BillId}) was pushed, but Tally did not list voucher {MasterId} when it was read back.",
                bill.InvoiceNumber, bill.Id, masterId);
            return null;
        }

        if (pushedToCompany is not null
            && !string.Equals(pushedToCompany, voucher.Company, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Invoice {InvoiceNumber} (bill {BillId}) was pushed to the Tally company '{PushedTo}', but its voucher was read back in '{ReadIn}'; the read is not used.",
                bill.InvoiceNumber, bill.Id, pushedToCompany, voucher.Company);
            return null;
        }

        // A voucher Tally shows no number for is not filed under the bill's either.
        var numberDiffers = !string.Equals(voucher.Number, bill.InvoiceNumber, StringComparison.Ordinal);
        var filedRow = auditStore.Write(
            bill.Id,
            BillAuditEvents.TallyVoucherFiled,
            bill.State,
            DateTimeOffset.UtcNow,
            new
            {
                tallyMasterId = voucher.MasterId,
                voucherNumberInTally = voucher.Number,
                voucherDateInTally = voucher.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                totalInTally = voucher.Amount,
                invoiceNumber = bill.InvoiceNumber,
                numberDiffers,
                // The tally.posted row of the push this read belongs to: the Bills list uses the
                // read only while that push is the latest word on what Tally holds.
                pushAuditId
            });
        try
        {
            using var saveCts = new CancellationTokenSource(SettleBudget, _timeProvider);
            await dbContext.SaveChangesAsync(saveCts.Token);
        }
        catch (Exception ex)
        {
            // Let the row go: a batch push saves the next bill on this same context, and a row
            // left behind would be saved, or fail, with it.
            dbContext.Entry(filedRow).State = EntityState.Detached;
            logger.LogWarning(ex, "The number Tally filed bill {BillId} under could not be recorded.", bill.Id);
        }

        if (numberDiffers)
        {
            logger.LogWarning(
                "Tally filed the voucher of invoice {InvoiceNumber} (bill {BillId}) as {VoucherNumber}.",
                bill.InvoiceNumber, bill.Id, voucher.Number);
        }

        return voucher.Number;
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
        var cancelled = false;

        foreach (var billId in orderedBillIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Stop starting new pushes; any in-flight one has already settled.
                cancelled = true;
                break;
            }

            try
            {
                var response = await PushInternalAsync(
                    billId,
                    reason,
                    requireTallyPreflight: false,
                    repostKey: null,
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
                    null,
                    response.TallyVoucherNumber));
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller went away before this bill was sent (once a bill
                // is claimed its outcome is settled regardless). That is the
                // batch being cut short, not this bill failing.
                dbContext.ChangeTracker.Clear();
                cancelled = true;
                break;
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
            StoppedOnFailure: failedBillId is not null,
            failedBillId,
            failureMessage,
            items,
            Cancelled: cancelled);
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

    /// <summary>
    /// Null when the voucher shows the bill as Tally was last said to hold it
    /// (that revision's date and grand total), otherwise the failure to settle
    /// the push with. Used where the voucher's identity is not certain: one
    /// found by number, which Auto Renumber can move onto another sale, and
    /// one found by an id from a push that did not record its company, which
    /// another company may have given to another sale.
    /// </summary>
    private static TallyPostResponse? Mismatch(
        BillTallyHistory.AlterHistory history,
        TallyVoucherRef voucher,
        string whichVoucher)
    {
        if (history.BillDate is { } heldDate && history.GrandTotal is { } heldTotal
            && voucher.Date == heldDate
            && voucher.Amount is { } amount
            && decimal.Round(amount, 2) == decimal.Round(heldTotal, 2))
        {
            return null;
        }

        var billAsHeld = history.BillDate is { } d
            ? $"dated {d:dd-MM-yyyy} for {FormatAmount(history.GrandTotal)}"
            : "of a date and amount that cannot be told";
        return TallyPostResponse.Failed(
            "TALLY_VOUCHER_MISMATCH",
            $"{whichVoucher} is dated {voucher.Date:dd-MM-yyyy} for {FormatAmount(voucher.Amount)} in Tally, "
            + $"but this bill was {billAsHeld} when Tally was last said to hold it, "
            + "so it may be another sale and nothing was sent. Check Tally: change the voucher there by hand and use Mark as Pushed, "
            + "or, if the bill's voucher is not there at all, use Mark as Pending and push the bill again.");
    }

    private static string FormatAmount(decimal? amount) =>
        amount is { } value ? value.ToString("0.00", CultureInfo.InvariantCulture) : "an unknown amount";

    /// <summary>
    /// The voucher an edited bill's push has to alter: its master id, the
    /// date it has in Tally now and where it was read, or the definite
    /// failure to settle the push with when it cannot be told.
    /// </summary>
    private readonly record struct AlterTarget(
        string? MasterId,
        DateOnly? VoucherDate,
        string? LookedUpNumber,
        TallyVoucherDestination? Destination,
        TallyPostResponse? Failure)
    {
        public static AlterTarget Fail(TallyPostResponse failure, string? lookedUpNumber = null) =>
            new(null, null, lookedUpNumber, null, failure);
    }

    /// <summary>
    /// Finds the voucher to alter, and always asks Tally for it: an Alter
    /// names its voucher by master id and by the date the voucher has in
    /// Tally now, and a voucher that has gone from Tally must stop the push,
    /// because Tally answers an Alter it cannot match by creating a second
    /// voucher. A push that succeeded from here recorded the voucher's id
    /// (and, since 25 November 2026, its company), and Tally is asked for
    /// that voucher. A bill that an admin marked as pushed has no such
    /// record: its voucher was entered in Tally by hand, or came from a push
    /// whose answer was lost. Tally is then asked which voucher carries the
    /// bill's number, and only exactly one answer that shows the bill as it
    /// was marked is acted on. Anything else stops the push before anything
    /// is sent.
    /// </summary>
    private async Task<AlterTarget> FindAlterTargetAsync(BillEntity bill, CancellationToken cancellationToken)
    {
        if (tallyVoucherLookup is null)
        {
            return AlterTarget.Fail(TallyPostResponse.Failed(
                "TALLY_ALTER_TARGET_MISSING",
                "Edited bill cannot be pushed because Tally cannot be asked for the voucher it has to change."));
        }

        var destination = await tallyVoucherLookup.DestinationAsync(cancellationToken);
        var history = await BillTallyHistory.ReadForAlterAsync(dbContext, bill, cancellationToken);

        // A master id names a voucher only within its company: in another
        // company the same id is somebody else's sale, and a miss there says
        // nothing about the bill's voucher. So the company comes first.
        if (history.Company is { } pushedTo
            && !string.Equals(pushedTo, destination.Company, StringComparison.OrdinalIgnoreCase))
        {
            return AlterTarget.Fail(TallyPostResponse.Failed(
                "TALLY_COMPANY_CHANGED",
                $"This bill was pushed to the Tally company '{pushedTo}', and Settings now name '{destination.Company}', so its voucher cannot be changed from here and nothing was sent. "
                + $"Choose '{pushedTo}' in Settings → Connection and push again, or change the voucher in Tally by hand and use Mark as Pushed."));
        }

        var fiscalYear = bill.FiscalYear!;
        if (history.MasterId is { } recordedId)
        {
            var voucher = await AskTallyAsync(
                () => tallyVoucherLookup.FindByMasterIdAsync(recordedId, fiscalYear, cancellationToken),
                $"Tally push blocked: this bill was edited after it was pushed, and Tally could not be asked for the voucher it was pushed as (master id {recordedId}), which the push has to change.",
                cancellationToken);
            if (voucher is null)
            {
                return AlterTarget.Fail(TallyPostResponse.Failed(
                    "TALLY_VOUCHER_NOT_FOUND",
                    $"Tally no longer has the sales voucher this bill was pushed as (master id {recordedId}) in {fiscalYear}, so there is nothing to change and nothing was sent. "
                    + "Check Tally: if that voucher was deleted there, use Mark as Pending and push the bill again."));
            }

            // A push recorded before 25 November 2026 does not say which
            // company it went to, so its id is held to the bill as well.
            if (history.Company is null
                && Mismatch(history, voucher, $"The voucher this bill was pushed as (master id {recordedId})") is { } mismatch)
            {
                return AlterTarget.Fail(mismatch);
            }

            return new AlterTarget(voucher.MasterId, voucher.Date, null, destination, null);
        }

        var number = history.VoucherNumber;
        var vouchers = await AskTallyAsync(
            () => tallyVoucherLookup.FindByNumberAsync(number, fiscalYear, cancellationToken),
            $"Tally push blocked: this bill was edited after it was marked as pushed, and Tally could not be asked for voucher {number}, which the push has to change.",
            cancellationToken);

        switch (vouchers.Count)
        {
            case 1:
                // A number is not a voucher's identity: when the voucher type
                // numbers vouchers itself (Automatic / Auto Renumber), an
                // insertion moves every later number, and the one voucher now
                // carrying this number can be another sale.
                if (Mismatch(history, vouchers[0], $"The voucher Tally has numbered {number}") is { } mismatch)
                {
                    return AlterTarget.Fail(mismatch, number);
                }

                logger.LogInformation(
                    "Voucher {VoucherNumber} for bill {BillId} was found in Tally as master id {MasterId}.",
                    number, bill.Id, vouchers[0].MasterId);
                return new AlterTarget(vouchers[0].MasterId, vouchers[0].Date, number, destination, null);
            case 0:
                return AlterTarget.Fail(TallyPostResponse.Failed(
                    "TALLY_VOUCHER_NOT_FOUND",
                    $"Tally has no sales voucher numbered {number} in {fiscalYear}, so there is nothing to change and nothing was sent. "
                    + $"Check Tally: if the voucher is there under another number, change its number to {number} and retry. "
                    + "If it is not there at all, use Mark as Pending and push the bill again."), number);
            default:
                return AlterTarget.Fail(TallyPostResponse.Failed(
                    "TALLY_VOUCHER_AMBIGUOUS",
                    $"Tally has {vouchers.Count} sales vouchers numbered {number} in {fiscalYear}, so the one to change cannot be told apart and nothing was sent. "
                    + "Remove or renumber the extra one in Tally, then retry."), number);
        }
    }

    /// <summary>
    /// Runs a read of Tally made before the bill is claimed. Nothing was sent
    /// and the bill is untouched when it fails, so the failure is reported the
    /// way a failed preflight is: try again when Tally answers.
    /// </summary>
    private static async Task<T> AskTallyAsync<T>(
        Func<Task<T>> read,
        string blockedMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TallyPreflightUnavailableException($"{blockedMessage} {ex.Message}");
        }
    }
}
