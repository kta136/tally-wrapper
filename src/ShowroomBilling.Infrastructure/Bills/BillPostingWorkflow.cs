using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillPostingWorkflow(
    ShowroomBillingDbContext dbContext,
    ITallyPoster tallyPoster,
    BillAuditStore auditStore,
    ILogger<BillPostingWorkflow> logger,
    ITallyCompanyHealthService? tallyCompanyHealthService = null)
{
    private const string DefaultShowroomCode = "default";
    private const string TallyAlterTargetTagName = "MASTER ID";

    internal Task<BillResponse> PushAsync(
        Guid billId,
        PushBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PushInternalAsync(
            billId,
            request.FiscalYear,
            request.Reason,
            broadcastHint: true,
            requireTallyPreflight: true,
            cancellationToken);
    }

    internal Task<BillBatchPushResponse> PushSelectedAsync(
        PushSelectedBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orderedIds = NormalizeOrderedIds(request.BillIds);
        return PushBatchAsync(orderedIds, request.FiscalYear, request.Reason, cancellationToken);
    }

    internal async Task<BillBatchPushResponse> PushPendingAsync(
        PushPendingBillsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var maxBills = Math.Clamp(request.MaxBills ?? 200, 1, 500);
        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var targetIds = await dbContext.Bills
            .AsNoTracking()
            .Where(x => x.ShowroomId == showroomId && (x.State == IBillService.StatePending || x.State == BillStates.Draft))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .Take(maxBills)
            .ToListAsync(cancellationToken);

        return await PushBatchAsync(targetIds, request.FiscalYear, request.Reason, cancellationToken);
    }

    internal async Task<BillPostingStatusResponse> RetryAsync(
        Guid billId,
        RetryBillPostingRequest request,
        CancellationToken cancellationToken = default)
    {
        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State != IBillService.StateFailed)
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; retry is only allowed from 'failed'.");
        }
        await EnsureTallyReadyAsync(cancellationToken);
        await PushInternalAsync(
            billId,
            fiscalYearInput: null,
            reason: request?.Reason ?? "retry",
            broadcastHint: false,
            requireTallyPreflight: false,
            cancellationToken);
        return (await auditStore.GetPostingStatusAsync(billId, cancellationToken))!;
    }

    internal async Task<BillPostingStatusResponse> RepostAsync(
        Guid billId,
        RepostBillRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        if (bill.State is not (IBillService.StatePosted or IBillService.StateFailed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; repost is only allowed from 'posted' or 'failed'.");
        }

        await EnsureTallyReadyAsync(cancellationToken);

        if (bill.State == IBillService.StatePosted)
        {
            // Briefly flip to pending so PushInternalAsync accepts it.
            bill.State = IBillService.StatePending;
            bill.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        await PushInternalAsync(
            billId,
            fiscalYearInput: null,
            reason: request.Reason ?? "repost",
            broadcastHint: false,
            requireTallyPreflight: false,
            cancellationToken);
        return (await auditStore.GetPostingStatusAsync(billId, cancellationToken))!;
    }

    private async Task<BillResponse> PushInternalAsync(
        Guid billId,
        string? fiscalYearInput,
        string? reason,
        bool broadcastHint,
        bool requireTallyPreflight,
        CancellationToken cancellationToken)
    {
        _ = broadcastHint;
        _ = fiscalYearInput;

        var bill = await LoadTrackedBillAsync(billId, cancellationToken);
        var currentRevision = bill.CurrentRevisionId is Guid id
            ? await dbContext.BillRevisions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            : null;

        // Already-posted / mid-posting short-circuits.
        if (bill.State is IBillService.StatePosting)
        {
            return BillSerialization.MapResponse(bill, currentRevision);
        }

        // Allowed: pending / draft / failed (retry goes through here too).
        if (bill.State is not (IBillService.StatePending or BillStates.Draft or IBillService.StateFailed))
        {
            throw new BillStateConflictException(
                $"Bill '{billId}' is in state '{bill.State}'; only pending/draft/failed bills can be pushed.");
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

        // Flip to `posting` atomically BEFORE calling Tally. Two concerns:
        //   1. Race: two concurrent push requests on the same bill must not both
        //      reach Tally. A conditional UPDATE gated on the pre-flip states
        //      ensures only one caller wins.
        //   2. Crash recovery: `posting` must be visible in the DB before the
        //      Tally round-trip, so StuckPostingRecoveryHostedService can heal
        //      a stuck row if the API crashes mid-call. Do NOT wrap this flip
        //      and the Tally call in a single transaction.
        if (UsesInMemoryProvider())
        {
            // ExecuteUpdateAsync is not reliably supported on the InMemory provider
            // used in unit tests, and those tests don't simulate real concurrency
            // (see CLAUDE.md "Test-harness reality"). Fall back to the original
            // mutate-then-save pattern for the in-memory code path only.
            bill.State = IBillService.StatePosting;
            bill.UpdatedAtUtc = now;
        }
        else
        {
            var rowsAffected = await dbContext.Bills
                .Where(b => b.Id == billId
                    && (b.State == IBillService.StatePending
                        || b.State == BillStates.Draft
                        || b.State == IBillService.StateFailed))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.State, IBillService.StatePosting)
                    .SetProperty(b => b.UpdatedAtUtc, now),
                    cancellationToken);

            if (rowsAffected == 0)
            {
                // Another concurrent push won the flip (or state drifted).
                // Reload and return whatever is current; caller sees the same
                // shape as the "already posting" short-circuit above.
                dbContext.ChangeTracker.Clear();
                var refreshed = await LoadTrackedBillAsync(billId, cancellationToken);
                var refreshedRevision = refreshed.CurrentRevisionId is Guid rid
                    ? await dbContext.BillRevisions.FirstOrDefaultAsync(x => x.Id == rid, cancellationToken)
                    : null;
                return BillSerialization.MapResponse(refreshed, refreshedRevision);
            }

            // Sync tracked entity snapshot with the row we just updated so the
            // final settle-state SaveChanges does not replay the posting flip.
            await dbContext.Entry(bill).ReloadAsync(cancellationToken);
        }

        currentRevision.SubmittedAtUtc ??= now;
        currentRevision.FinalizedAtUtc = now;

        var operation = bill.EditedAfterPush ? TallyPostOperation.Alter : TallyPostOperation.Create;
        var targetTagName = operation == TallyPostOperation.Alter ? TallyAlterTargetTagName : null;
        string? targetTagValue = null;
        if (bill.EditedAfterPush)
        {
            targetTagValue = await ResolveAlterTargetMasterIdAsync(bill.Id, cancellationToken);
        }

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? "manual-push" : reason.Trim();
        auditStore.Write(
            bill.Id,
            "bill.push.requested",
            bill.State,
            now,
            new
            {
                invoiceNumber = bill.InvoiceNumber,
                fiscalYear = bill.FiscalYear,
                reason = trimmedReason,
                tallyAction = operation.ToString()
            });
        await dbContext.SaveChangesAsync(cancellationToken);

        // Call Tally synchronously. No queue, no retry.
        var payload = BillSerialization.DeserializePayload(currentRevision);
        var idempotencyKey = $"post:{bill.Id:N}:{currentRevision.Id:N}";
        TallyPostRequest? postRequest = null;

        TallyPostResponse tallyResult;
        if (operation == TallyPostOperation.Alter && targetTagValue is null)
        {
            tallyResult = new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_ALTER_TARGET_MISSING",
                ErrorMessage: "Edited bill cannot be pushed because the original Tally voucher master id is unavailable.",
                XmlShape: "voucher-import-v1",
                RequestExcerpt: null,
                ResponseExcerpt: null);
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
                // Treat anything that leaks out as a failed post; the bill settles on `failed`.
                tallyResult = new TallyPostResponse(
                    Outcome: TallyPostOutcome.Failed,
                    RemoteId: null,
                    ErrorCode: "TALLY_UNEXPECTED",
                    ErrorMessage: ex.Message,
                    XmlShape: "voucher-import-v1",
                    RequestExcerpt: null,
                    ResponseExcerpt: null);
            }
        }

        var settleAt = DateTimeOffset.UtcNow;
        if (tallyResult.Outcome == TallyPostOutcome.Posted)
        {
            bill.State = IBillService.StatePosted;
            if (operation == TallyPostOperation.Alter)
            {
                bill.EditedAfterPush = false;
            }
            bill.UpdatedAtUtc = settleAt;
            auditStore.Write(
                bill.Id,
                "tally.posted",
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
        else
        {
            bill.State = IBillService.StateFailed;
            bill.UpdatedAtUtc = settleAt;
            auditStore.Write(
                bill.Id,
                "tally.failed",
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

        await dbContext.SaveChangesAsync(cancellationToken);
        if (tallyResult.Outcome == TallyPostOutcome.Posted && operation == TallyPostOperation.Alter)
        {
            await PurgePreEditAuditAsync(bill.Id, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return BillSerialization.MapResponse(bill, currentRevision);
    }

    private async Task<BillBatchPushResponse> PushBatchAsync(
        IReadOnlyList<Guid> orderedBillIds,
        string? fiscalYear,
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
            try
            {
                var response = await PushInternalAsync(
                    billId,
                    fiscalYear,
                    reason,
                    broadcastHint: false,
                    requireTallyPreflight: false,
                    cancellationToken);
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
        string.Equals(health.Status, "healthy", StringComparison.OrdinalIgnoreCase)
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
                && e.EventType == "bill.edit.reopened")
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
                && e.EventType == "tally.posted"
                && e.CreatedAtUtc < lastReopen.CreatedAtUtc)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastPreEditSuccess is null)
        {
            return null;
        }

        return NormalizePositiveInteger(ReadDetailsString(lastPreEditSuccess.PayloadJson, "tallyMasterId"))
            ?? NormalizePositiveInteger(ReadDetailsString(lastPreEditSuccess.PayloadJson, "remoteId"));
    }

    private async Task PurgePreEditAuditAsync(Guid billId, CancellationToken cancellationToken)
    {
        var billIdText = billId.ToString();
        var lastReopen = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && e.EventType == "bill.edit.reopened")
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastReopen is null)
        {
            return;
        }

        var cutoff = lastReopen.CreatedAtUtc;

        if (UsesInMemoryProvider())
        {
            // ExecuteDeleteAsync is unreliable on the InMemory provider used in unit tests.
            // Fall back to load-and-RemoveRange for that path only.
            var stale = await dbContext.AuditEvents
                .Where(e => e.EntityType == "bill"
                    && e.EntityId == billIdText
                    && e.CreatedAtUtc < cutoff)
                .ToListAsync(cancellationToken);
            if (stale.Count > 0)
            {
                dbContext.AuditEvents.RemoveRange(stale);
            }
            return;
        }

        await dbContext.AuditEvents
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && e.CreatedAtUtc < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<BillEntity> LoadTrackedBillAsync(Guid billId, CancellationToken cancellationToken)
    {
        var bill = await dbContext.Bills.FirstOrDefaultAsync(x => x.Id == billId, cancellationToken);
        if (bill is null)
        {
            throw new BillNotFoundException(billId);
        }
        return bill;
    }

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    private bool UsesInMemoryProvider() =>
        string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private static string? ReadDetailsString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty(propertyName, out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? NormalizePositiveInteger(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? trimmed
            : null;
    }

    private static IReadOnlyList<Guid> NormalizeOrderedIds(IReadOnlyList<Guid>? billIds)
    {
        if (billIds is null || billIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var seen = new HashSet<Guid>();
        var ordered = new List<Guid>(billIds.Count);
        foreach (var billId in billIds)
        {
            if (billId == Guid.Empty || !seen.Add(billId))
            {
                continue;
            }

            ordered.Add(billId);
        }

        return ordered;
    }
}
