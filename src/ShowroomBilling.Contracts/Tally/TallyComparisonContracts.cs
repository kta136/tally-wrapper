namespace ShowroomBilling.Contracts.Tally;

/// <summary>
/// The answer to <c>GET /api/tally-check/compare</c>: the bills of a fiscal year
/// that Tally should hold, each set against the sales voucher Tally has for it.
/// Only what differs is listed; <see cref="Matching"/> counts the rest.
/// </summary>
/// <param name="BillsInOtherCompany">
/// Bills whose last push went to another Tally company than the one Settings
/// name now. Their vouchers are in that company's books, so they are not compared.
/// </param>
/// <param name="PushedWhileComparing">
/// A bill was being pushed, or a push finished, while the comparison ran: its
/// bill or its voucher can show as a difference that is not one. Compare again.
/// </param>
/// <param name="MatchingWrittenOtherwise">
/// Of <see cref="Matching"/>, the bills whose voucher number Tally writes with
/// zeros in front of its digits ("…/051" for "…/51") or its letters in another
/// case. That is how Tally fills numbers, not another number, so they match.
/// </param>
public sealed record TallyComparisonResponse(
    string FiscalYear,
    DateTimeOffset ComparedAtUtc,
    int BillsCompared,
    int VouchersInTally,
    int Matching,
    IReadOnlyList<TallyComparisonDifference> Differences,
    int BillsInOtherCompany = 0,
    bool PushedWhileComparing = false,
    int MatchingWrittenOtherwise = 0);

/// <summary>
/// One bill whose voucher differs from it, one bill Tally should hold but does
/// not (<see cref="MissingFromTally"/>), or one sales voucher no bill accounts
/// for (<see cref="ExtraInTally"/>, with no bill fields). The bill's side is
/// the bill as Tally holds it: as it is now, or, when it was edited after Tally
/// took it, as it was then, so an edit not yet pushed is not reported as a
/// difference; for a bill waiting for reconciliation it is the bill as its
/// unsettled push sent it.
/// </summary>
/// <param name="NumberDiffers">
/// Tally files the voucher under another number. Zeros in front of its digits or
/// letters in another case are how Tally writes the same number, and do not count.
/// </param>
/// <param name="TallyMasterId">
/// The voucher's id in Tally. On a bill missing from Tally, the id its last push
/// recorded, or null when no push recorded one and the bill was looked for by number.
/// </param>
/// <param name="TallyTotal">The voucher's total; null when Tally gave none, and then <see cref="TotalDiffers"/> is true, since it could not be checked.</param>
/// <param name="SharedVoucher">The voucher is also the one another bill's push recorded: two bills claim one voucher.</param>
public sealed record TallyComparisonDifference(
    Guid? BillId,
    string? BillNumber,
    string? BillState,
    DateOnly? BillDate,
    decimal? BillTotal,
    string? TallyMasterId,
    string? TallyNumber,
    DateOnly? TallyDate,
    decimal? TallyTotal,
    bool NumberDiffers,
    bool DateDiffers,
    bool TotalDiffers,
    bool MissingFromTally,
    bool ExtraInTally,
    bool SharedVoucher = false);
