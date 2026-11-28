using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>
/// Settings → Tally check: sets a fiscal year's bills against the sales
/// vouchers Tally holds, and lists what differs. It only reads, from Tally (one
/// request for the whole year) and from the database, and changes nothing.
///
/// A bill is compared when Tally should hold its voucher: the same rule as
/// <see cref="BillTallyHistory"/> (posted, edited after a push, or last said to
/// be in Tally by a push or Mark as Pushed), plus a bill waiting for
/// reconciliation, which is exactly the bill this check helps settle. It is set
/// against the voucher its last push recorded, by master id, unless that push
/// went to another company than Settings name now; a bill no push from here
/// recorded an id for (marked as pushed by hand, or older than the record) is
/// matched by number instead, allowing zeros in front of its digits. A number
/// Tally writes with such zeros, or in another letter case, is the same number:
/// it is counted as matching (<see cref="TallyComparisonResponse.MatchingWrittenOtherwise"/>),
/// not listed. The bill's side is the bill as Tally holds it (see
/// <see cref="HeldRevision"/>), so an edit not yet pushed is not a difference.
/// A bill waiting for reconciliation is set against what its
/// unsettled push sent, since that push is what the operator has to settle: an
/// Alter by the voucher it named, a Create by the bill's number (an earlier
/// push's voucher says nothing about a fresh create).
/// </summary>
public sealed partial class TallyComparison(
    ShowroomBillingDbContext dbContext,
    ITallyVoucherLookup voucherLookup,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TallyComparisonResponse> CompareAsync(string? fiscalYear, CancellationToken cancellationToken = default)
    {
        var comparedAt = _timeProvider.GetUtcNow();
        var year = string.IsNullOrWhiteSpace(fiscalYear)
            ? InvoiceNumberFormatter.ComputeFiscalYear(comparedAt)
            : fiscalYear.Trim();
        if (!IsFiscalYear(year))
        {
            throw new RequestValidationException($"'{year}' is not a fiscal year such as 2026-27.");
        }

        TallyVoucherDestination destination;
        IReadOnlyList<TallyVoucherRef> vouchers;
        try
        {
            (destination, vouchers) = await voucherLookup.ListSalesVouchersAsync(year, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new TallyPreflightUnavailableException(
                $"Tally could not be asked for its sales vouchers of {year}: {ex.Message}");
        }

        var bills = await dbContext.Bills.AsNoTracking()
            .Where(b => b.FiscalYear == year && b.InvoiceNumber != null)
            .Select(b => new { b.Id, b.InvoiceNumber, b.State, b.EditedAfterPush, b.CurrentRevisionId })
            .ToListAsync(cancellationToken);
        var billIds = bills.Select(b => b.Id).ToList();
        var billIdTexts = bills.Select(b => b.Id.ToString()).ToList();

        var events = (await dbContext.AuditEvents.AsNoTracking()
                .Where(e => e.EntityType == "bill"
                    && billIdTexts.Contains(e.EntityId)
                    && (e.EventType == BillAuditEvents.TallyPosted
                        || e.EventType == BillAuditEvents.MarkPosted
                        || e.EventType == BillAuditEvents.MarkPending
                        || e.EventType == BillAuditEvents.NumberChanged
                        || e.EventType == BillAuditEvents.PushRequested
                        || e.EventType == BillAuditEvents.TallyOutcomeUnknown))
                .Select(e => new AuditFact(e.EntityId, e.EventType, e.CreatedAtUtc, e.Id, e.PayloadJson))
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.EntityId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.At).ThenBy(e => e.Id).ToList());

        var revisions = (await dbContext.BillRevisions.AsNoTracking()
                .Where(r => billIds.Contains(r.BillId))
                .Select(r => new RevisionFact(r.Id, r.BillId, r.CreatedAtUtc, r.RevisionNo, r.BillDate, r.GrandTotal))
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.BillId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Tally was read before the bills: a push that settled in between (even
        // with an outcome nobody knows), or is still going, can show as a bill
        // without its voucher or the reverse.
        var pushedWhileComparing = bills.Any(b => b.State == BillStates.Posting)
            || events.Values.Any(trail => trail.Any(e => e.At >= comparedAt
                && e.EventType is BillAuditEvents.TallyPosted or BillAuditEvents.MarkPosted
                    or BillAuditEvents.MarkPending or BillAuditEvents.TallyOutcomeUnknown));

        // What Tally should hold for each bill it should hold a voucher for.
        var expected = new List<ExpectedVoucher>();
        var inOtherCompany = 0;
        foreach (var bill in bills)
        {
            var trail = events.GetValueOrDefault(bill.Id.ToString()) ?? [];
            var lastTruth = trail.LastOrDefault(e => e.EventType is BillAuditEvents.TallyPosted
                or BillAuditEvents.MarkPosted or BillAuditEvents.MarkPending);
            var unsettled = bill.State == BillStates.ReconciliationRequired;
            var inTally = bill.State == BillStates.Posted
                || bill.EditedAfterPush
                || lastTruth?.EventType is BillAuditEvents.TallyPosted or BillAuditEvents.MarkPosted;
            if (!inTally && !unsettled)
            {
                continue;
            }

            var recorded = RecordedPush(trail);
            if (unsettled)
            {
                recorded = UnsettledTarget(trail, recorded);
            }

            if (recorded.Company is { } pushedTo && destination.Company is { } named
                && !string.Equals(pushedTo.Trim(), named.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                inOtherCompany++;
                continue;
            }

            var lastInTally = trail.LastOrDefault(e => e.EventType is BillAuditEvents.TallyPosted or BillAuditEvents.MarkPosted);
            var billRevisions = revisions.GetValueOrDefault(bill.Id) ?? [];
            var held = HeldRevision(billRevisions, bill.CurrentRevisionId, bill.EditedAfterPush && !unsettled, lastInTally);

            expected.Add(new ExpectedVoucher(
                bill.Id,
                bill.State,
                unsettled ? bill.InvoiceNumber! : NumberTallyHolds(bill.InvoiceNumber!, trail, lastInTally),
                held?.BillDate,
                held?.GrandTotal,
                recorded.MasterId));
        }

        // Set each against its voucher: by recorded id first, then by number.
        var byId = vouchers.ToDictionary(v => v.MasterId, StringComparer.Ordinal);
        var claimedById = new Dictionary<string, int>(StringComparer.Ordinal);
        var pairs = new List<(ExpectedVoucher Bill, TallyVoucherRef? Voucher)>();
        foreach (var bill in expected.Where(b => b.RecordedMasterId is not null))
        {
            var voucher = byId.GetValueOrDefault(bill.RecordedMasterId!);
            if (voucher is not null)
            {
                claimedById[voucher.MasterId] = claimedById.GetValueOrDefault(voucher.MasterId) + 1;
            }
            pairs.Add((bill, voucher));
        }

        var claimed = new HashSet<string>(claimedById.Keys, StringComparer.Ordinal);
        var byNumber = vouchers
            .Where(v => v.Number is not null)
            .GroupBy(v => InvoiceNumberFormatter.PaddingFreeKey(v.Number!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var bill in expected.Where(b => b.RecordedMasterId is null))
        {
            var candidates = (byNumber.GetValueOrDefault(InvoiceNumberFormatter.PaddingFreeKey(bill.Number)) ?? [])
                .Where(v => !claimed.Contains(v.MasterId))
                .ToList();
            // The bill's own date and total first, then its number written exactly, so that
            // which voucher is taken does not depend on the order Tally listed them in.
            var voucher = candidates
                .OrderByDescending(v => v.Date == bill.Date && SameTotal(v.Amount, bill.Total))
                .ThenByDescending(v => string.Equals(v.Number, bill.Number, StringComparison.Ordinal))
                .FirstOrDefault();
            if (voucher is not null)
            {
                claimed.Add(voucher.MasterId);
            }
            pairs.Add((bill, voucher));
        }

        var differences = new List<TallyComparisonDifference>();
        var matching = 0;
        var matchingWrittenOtherwise = 0;
        foreach (var (bill, voucher) in pairs)
        {
            if (voucher is null)
            {
                differences.Add(new TallyComparisonDifference(
                    bill.BillId, bill.Number, bill.State, bill.Date, bill.Total,
                    bill.RecordedMasterId, null, null, null,
                    NumberDiffers: false, DateDiffers: false, TotalDiffers: false,
                    MissingFromTally: true, ExtraInTally: false));
                continue;
            }

            // Zeros in front of the digits, or letters in another case, are how Tally writes the
            // number (its voucher type fills numbers to a width), not another number.
            var numberDiffers = !InvoiceNumberFormatter.SameIgnoringPadding(voucher.Number, bill.Number);
            var writtenOtherwise = !numberDiffers && !string.Equals(voucher.Number, bill.Number, StringComparison.Ordinal);
            // What could not be read on either side cannot be said to agree.
            var dateDiffers = bill.Date != voucher.Date;
            var totalDiffers = !SameTotal(voucher.Amount, bill.Total);
            var shared = bill.RecordedMasterId is not null && claimedById.GetValueOrDefault(voucher.MasterId) > 1;
            if (!numberDiffers && !dateDiffers && !totalDiffers && !shared)
            {
                matching++;
                if (writtenOtherwise)
                {
                    matchingWrittenOtherwise++;
                }
                continue;
            }

            differences.Add(new TallyComparisonDifference(
                bill.BillId, bill.Number, bill.State, bill.Date, bill.Total,
                voucher.MasterId, voucher.Number, voucher.Date, voucher.Amount,
                numberDiffers, dateDiffers, totalDiffers,
                MissingFromTally: false, ExtraInTally: false, SharedVoucher: shared));
        }

        foreach (var voucher in vouchers.Where(v => !claimed.Contains(v.MasterId)))
        {
            differences.Add(new TallyComparisonDifference(
                null, null, null, null, null,
                voucher.MasterId, voucher.Number, voucher.Date, voucher.Amount,
                NumberDiffers: false, DateDiffers: false, TotalDiffers: false,
                MissingFromTally: false, ExtraInTally: true));
        }

        return new TallyComparisonResponse(
            year,
            comparedAt,
            expected.Count,
            vouchers.Count,
            matching,
            differences
                .OrderBy(d => d.BillDate ?? d.TallyDate)
                .ThenBy(d => d.BillNumber ?? d.TallyNumber, StringComparer.Ordinal)
                .ToList(),
            inOtherCompany,
            pushedWhileComparing,
            matchingWrittenOtherwise);
    }

    /// <summary>
    /// The revision Tally holds. A bill not edited since Tally took it is held as it is now: an
    /// edit of a bill Tally holds marks it edited after the push, and only a push or Mark as Pushed
    /// clears that. An edited bill is held as it was when Tally was last said to hold it. The time
    /// is asked only then, because a bill brought over from V1 can have its revisions dated after
    /// the push that put it in Tally; with none that early the bill's first revision stands in.
    /// </summary>
    private static RevisionFact? HeldRevision(
        List<RevisionFact> revisions,
        Guid? currentRevisionId,
        bool editedSinceTallyTookIt,
        AuditFact? lastInTally)
    {
        if (!editedSinceTallyTookIt || lastInTally is null)
        {
            return revisions.FirstOrDefault(r => r.Id == currentRevisionId);
        }

        return revisions
                .Where(r => r.CreatedAt <= lastInTally.At)
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.RevisionNo)
                .FirstOrDefault()
            ?? revisions.OrderBy(r => r.CreatedAt).ThenBy(r => r.RevisionNo).FirstOrDefault();
    }

    /// <summary>
    /// The voucher the bill's last successful push recorded, and the company it
    /// went to, unless a Mark as Pending since has said Tally no longer holds
    /// it. Only <c>tallyMasterId</c> names it; <c>remoteId</c> stands in only on
    /// an event written before <c>tallyMasterId</c> was recorded, since later it
    /// can be a repost's own key (as in <see cref="BillPostingWorkflow"/>).
    /// </summary>
    private static (string? MasterId, string? Company) RecordedPush(List<AuditFact> trail)
    {
        var lastPending = trail.LastOrDefault(e => e.EventType == BillAuditEvents.MarkPending);
        var push = trail.LastOrDefault(e => e.EventType == BillAuditEvents.TallyPosted
            && (lastPending is null || e.At > lastPending.At));
        return BillTallyHistory.RecordedVoucher(push?.PayloadJson);
    }

    /// <summary>
    /// What a bill waiting for reconciliation should be found by: the voucher its
    /// unsettled push named when that push was an Alter (from the attempt, or the
    /// push it altered when the attempt is older than that record), and nothing
    /// when it was a Create, which is looked for by the bill's number.
    /// </summary>
    private static (string? MasterId, string? Company) UnsettledTarget(List<AuditFact> trail, (string? MasterId, string? Company) recorded)
    {
        var attempt = trail.LastOrDefault(e => e.EventType == BillAuditEvents.PushRequested);
        var action = BillAuditStore.ReadDetailsString(attempt?.PayloadJson, "tallyAction");
        if (!string.Equals(action, "Alter", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        return TallyIds.NormalizePositiveInteger(BillAuditStore.ReadDetailsString(attempt!.PayloadJson, "targetTagValue")) is { } target
            ? (target, recorded.MasterId == target ? recorded.Company : null)
            : recorded;
    }

    /// <summary>
    /// The number the voucher carries in Tally as far as the app knows: the
    /// bill's number when Tally last held it. A rename since then has not
    /// reached Tally, so the first rename after that moment holds the number.
    /// </summary>
    private static string NumberTallyHolds(string current, List<AuditFact> trail, AuditFact? lastInTally)
    {
        if (lastInTally is null)
        {
            return current;
        }

        var firstRename = trail.FirstOrDefault(e => e.EventType == BillAuditEvents.NumberChanged && e.At > lastInTally.At);
        return BillTallyHistory.NumberBeforeRename(current, firstRename?.PayloadJson);
    }

    private static bool SameTotal(decimal? tally, decimal? bill) =>
        tally is { } t && bill is { } b && decimal.Round(t, 2) == decimal.Round(b, 2);

    /// <summary>"2026-27": a start year and the last two digits of the year after it.</summary>
    private static bool IsFiscalYear(string label)
    {
        var match = FiscalYearLabel().Match(label);
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && start is >= 2000 and <= 2098
            && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) == (start + 1) % 100;
    }

    // ASCII digits only: \d would let in other scripts' digits, which the parse below refuses.
    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})$")]
    private static partial Regex FiscalYearLabel();

    private sealed record AuditFact(string EntityId, string EventType, DateTimeOffset At, Guid Id, string? PayloadJson);

    private sealed record RevisionFact(Guid Id, Guid BillId, DateTimeOffset CreatedAt, int RevisionNo, DateOnly? BillDate, decimal GrandTotal);

    private sealed record ExpectedVoucher(
        Guid BillId,
        string State,
        string Number,
        DateOnly? Date,
        decimal? Total,
        string? RecordedMasterId);
}
