using System.Net;
using System.Net.Http;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Handover;

/// <summary>What happened to one bill of a hand-over, or to a bill an earlier hand-over made that this one left out.</summary>
internal enum HandoverBillResult
{
    Saved,
    Updated,
    Unchanged,
    NotSaved,
    NotUpdated,

    /// <summary>Not updated because the operator skipped the admin passcode it needed.</summary>
    Skipped,
    NotInHandover,
}

internal enum HandoverPrintResult
{
    None,
    Printed,
    Failed,
}

/// <param name="Retryable">The server could not be reached, or answered that it could not do it now: Try again may work.</param>
internal sealed record HandoverBillOutcome(
    Guid SaveId,
    string? InvoiceNumber,
    string PartyName,
    decimal? Amount,
    string Payment,
    HandoverBillResult Result,
    HandoverPrintResult Print = HandoverPrintResult.None,
    string? Reason = null,
    BillResponse? Bill = null,
    bool Retryable = false);

/// <summary>Every bill's outcome, in the hand-over's order, then the bills it left out.</summary>
/// <param name="ServerUnreachable">The server stopped answering; the bills after that point were not tried.</param>
internal sealed record HandoverRun(IReadOnlyList<HandoverBillOutcome> Outcomes, bool ServerUnreachable)
{
    public bool CanTryAgain => ServerUnreachable || Outcomes.Any(outcome => outcome.Retryable);
}

/// <summary>
/// Saves the bills of a hand-over one after another, through the same create and update calls the
/// Invoice tab makes. A bill is found again by its save ID (the create's operation id): not there,
/// it is created and printed; there with the same content, it is left alone; there and different,
/// it is updated with the revision it was found at and printed, asking for the admin passcode first
/// when the bill is in Tally. A voided or revised bill, or one whose push is not settled, is
/// reported and left alone. The bills an earlier hand-over made that this one left out are only
/// reported.
/// </summary>
internal sealed class HandoverSaveWorkflow(
    IBillsApiClient bills,
    Func<BillResponse, string, CancellationToken, Task<string?>> requestAdminToken,
    Func<BillResponse, Task<bool>> print)
{
    public const string ServerUnreachableReason = "The server can't be reached.";

    /// <summary>
    /// Save IDs whose create or update was sent without an answer. Found again with the same
    /// content on Try again, such a bill was saved by that attempt and still needs printing.
    /// </summary>
    private readonly Dictionary<Guid, HandoverBillResult> _unanswered = [];

    public async Task<HandoverRun> RunAsync(HandoverDocument document, HandoverMasters masters, CancellationToken cancellationToken)
    {
        var outcomes = new List<HandoverBillOutcome>(document.Bills.Count);
        var unreachable = false;
        foreach (var bill in document.Bills)
        {
            if (unreachable)
            {
                outcomes.Add(NotSaved(bill, ServerUnreachableReason, retryable: true));
                continue;
            }

            var outcome = await SaveAsync(bill, masters, cancellationToken);
            outcomes.Add(outcome.Outcome);
            unreachable = outcome.Unreachable;
        }

        foreach (var saveId in document.NotSent ?? [])
        {
            if (unreachable)
            {
                break;
            }

            try
            {
                var left = await bills.GetByOperationAsync(saveId, cancellationToken);
                if (left is not null && !BillStateIs(left, BillStates.Voided))
                {
                    outcomes.Add(new HandoverBillOutcome(
                        saveId,
                        left.InvoiceNumber,
                        left.CurrentRevision?.Payload.PartyName ?? string.Empty,
                        left.CurrentRevision?.Payload.Totals.GrandTotal,
                        left.CurrentRevision?.Payload.Payment ?? string.Empty,
                        HandoverBillResult.NotInHandover,
                        Reason: "Void it in Bills if it’s no longer wanted.",
                        Bill: left));
                }
            }
            catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
            {
                unreachable = true;
            }
        }

        return new HandoverRun(outcomes, unreachable);
    }

    private async Task<(HandoverBillOutcome Outcome, bool Unreachable)> SaveAsync(
        HandoverBill bill,
        HandoverMasters masters,
        CancellationToken cancellationToken)
    {
        BillResponse? existing;
        try
        {
            existing = await bills.GetByOperationAsync(bill.SaveId, cancellationToken);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            return (NotSaved(bill, ServerUnreachableReason, retryable: true), true);
        }
        catch (HttpRequestException ex)
        {
            return (NotSaved(bill, ApiResponseReader.FormatError(ex), retryable: IsServerBusy(ex)), false);
        }

        var mapped = HandoverBillMapper.Map(bill, masters);
        if (existing is null)
        {
            if (mapped.Payload is not { } payload)
            {
                return (NotSaved(bill, mapped.Problem), false);
            }

            return await CreateAsync(bill, payload, cancellationToken);
        }

        if (mapped.Payload is not { } changed)
        {
            return (NotUpdated(bill, existing, mapped.Problem), false);
        }

        if (BillStateIs(existing, BillStates.Voided))
        {
            return (NotUpdated(bill, existing, "It was voided in Bills, so it was left as it is."), false);
        }

        if (BillStateIs(existing, BillStates.Revised))
        {
            return (NotUpdated(bill, existing, "It was revised into another bill in Bills, so it was left as it is."), false);
        }

        if (existing.CurrentRevision?.Payload is { } current && HandoverPayloads.SameContent(current, changed))
        {
            if (_unanswered.Remove(bill.SaveId, out var landed))
            {
                // The save went through on the earlier try; only its answer was lost.
                return (await PrintedAsync(bill, existing, landed), false);
            }

            return (Outcome(bill, existing, HandoverBillResult.Unchanged), false);
        }

        if (BillStateIs(existing, BillStates.Posting) || BillStateIs(existing, BillStates.ReconciliationRequired))
        {
            return (NotUpdated(bill, existing, "Its push to Tally isn't settled. Check it in Bills, then send it again."), false);
        }

        string? adminToken = null;
        if (BillStateCapabilities.IsPosted(existing.State))
        {
            var context = $"{existing.InvoiceNumber} · {bill.PartyName.Trim()} is in Tally. Updating it needs the admin passcode.";
            adminToken = await requestAdminToken(existing, context, cancellationToken);
            if (adminToken is null)
            {
                return (Outcome(bill, existing, HandoverBillResult.Skipped) with
                {
                    Reason = "Skipped. It is in Tally, and updating it needs the admin passcode.",
                }, false);
            }
        }

        BillResponse updated;
        try
        {
            _unanswered[bill.SaveId] = HandoverBillResult.Updated;
            updated = await bills.UpdateDraftAsync(
                existing.Id,
                new UpdateBillDraftRequest(changed, existing.CurrentRevisionId),
                adminToken,
                cancellationToken);
            _unanswered.Remove(bill.SaveId);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            return (NotUpdated(bill, existing, ServerUnreachableReason, retryable: true), true);
        }
        catch (HttpRequestException ex)
        {
            _unanswered.Remove(bill.SaveId);
            var reason = ex is ApiException { StatusCode: HttpStatusCode.Forbidden }
                ? "It is in Tally, and updating it needs the admin passcode."
                : ApiResponseReader.FormatError(ex);
            return (NotUpdated(bill, existing, reason, IsServerBusy(ex)), false);
        }

        return (await PrintedAsync(bill, updated, HandoverBillResult.Updated), false);
    }

    private async Task<(HandoverBillOutcome Outcome, bool Unreachable)> CreateAsync(
        HandoverBill bill,
        BillPayloadDto payload,
        CancellationToken cancellationToken)
    {
        BillResponse created;
        try
        {
            _unanswered[bill.SaveId] = HandoverBillResult.Saved;
            created = await bills.CreateDraftAsync(new CreateBillDraftRequest(null, payload, bill.SaveId), cancellationToken);
            _unanswered.Remove(bill.SaveId);
        }
        catch (Exception ex) when (IsUnreachable(ex, cancellationToken))
        {
            return (NotSaved(bill, ServerUnreachableReason, retryable: true, payload), true);
        }
        catch (HttpRequestException ex)
        {
            _unanswered.Remove(bill.SaveId);
            return (NotSaved(bill, ApiResponseReader.FormatError(ex), IsServerBusy(ex), payload), false);
        }

        return (await PrintedAsync(bill, created, HandoverBillResult.Saved), false);
    }

    private async Task<HandoverBillOutcome> PrintedAsync(HandoverBill bill, BillResponse saved, HandoverBillResult result)
    {
        bool printed;
        try
        {
            printed = await print(saved);
        }
        catch (Exception)
        {
            printed = false;
        }

        return Outcome(bill, saved, result) with { Print = printed ? HandoverPrintResult.Printed : HandoverPrintResult.Failed };
    }

    private static HandoverBillOutcome Outcome(HandoverBill bill, BillResponse found, HandoverBillResult result) => new(
        bill.SaveId,
        found.InvoiceNumber,
        bill.PartyName.Trim(),
        found.CurrentRevision?.Payload.Totals.GrandTotal,
        bill.Payment,
        result,
        Bill: found);

    private static HandoverBillOutcome NotUpdated(HandoverBill bill, BillResponse found, string? reason, bool retryable = false)
        => Outcome(bill, found, HandoverBillResult.NotUpdated) with { Reason = reason, Retryable = retryable };

    private static HandoverBillOutcome NotSaved(HandoverBill bill, string? reason, bool retryable = false, BillPayloadDto? payload = null) => new(
        bill.SaveId,
        null,
        bill.PartyName.Trim(),
        payload?.Totals.GrandTotal ?? HandoverBillMapper.TotalsOf(bill.Lines.Select(line => line.LineTotal).ToList(), bill.FinalAmount).GrandTotal,
        bill.Payment,
        HandoverBillResult.NotSaved,
        Reason: reason,
        Retryable: retryable);

    private static bool BillStateIs(BillResponse bill, string state)
        => string.Equals(bill.State, state, StringComparison.Ordinal);

    /// <summary>No answer at all: the connection failed or the call timed out.</summary>
    internal static bool IsUnreachable(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            ApiException => false,
            HttpRequestException => true,
            TaskCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };

    /// <summary>The server answered that it cannot do it now (its database or Tally is down): worth trying again.</summary>
    private static bool IsServerBusy(HttpRequestException exception)
        => exception.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}

/// <summary>Whether two payloads say the same thing, field by field, whatever their decimals' trailing zeros.</summary>
internal static class HandoverPayloads
{
    public static bool SameContent(BillPayloadDto stored, BillPayloadDto handedOver)
    {
        IReadOnlyList<BillLineItemDto> none = [];
        return stored with { Lines = none } == handedOver with { Lines = none }
               && stored.Lines.SequenceEqual(handedOver.Lines);
    }
}
