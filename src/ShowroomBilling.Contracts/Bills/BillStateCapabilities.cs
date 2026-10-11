namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// Shared bill-state capability rules for UI affordances and client-side guards.
/// Server workflows remain authoritative, but callers should use these helpers
/// instead of repeating state-string combinations.
/// </summary>
public static class BillStateCapabilities
{
    public static bool IsKnown(string? state) =>
        IsPendingLike(state)
        || IsPosting(state)
        || IsPosted(state)
        || IsFailed(state)
        || IsReconciliationRequired(state)
        || IsRevised(state)
        || IsVoided(state);

    public static bool IsPendingLike(string? state) =>
        IsState(state, BillStates.Pending) || IsState(state, BillStates.Draft);

    private static bool IsPosting(string? state) => IsState(state, BillStates.Posting);

    public static bool IsPosted(string? state) => IsState(state, BillStates.Posted);

    public static bool IsFailed(string? state) => IsState(state, BillStates.Failed);

    private static bool IsReconciliationRequired(string? state) =>
        IsState(state, BillStates.ReconciliationRequired);

    private static bool IsRevised(string? state) => IsState(state, BillStates.Revised);

    private static bool IsVoided(string? state) => IsState(state, BillStates.Voided);

    public static bool CanPush(string? state) => IsPendingLike(state);

    public static bool CanRetry(string? state) => IsFailed(state);

    public static bool CanRepost(string? state) => IsPosted(state) || IsFailed(state);

    public static bool CanRevise(string? state) => IsPendingLike(state);

    public static bool CanVoid(string? state) => IsPendingLike(state) || IsFailed(state);

    public static bool CanEdit(string? state) => IsPendingLike(state) || IsFailed(state) || IsPosted(state);

    public static bool CanChangeNumber(string? state) => HasNoOpenTallyOutcome(state);

    public static bool CanDelete(string? state) => HasNoOpenTallyOutcome(state);

    public static bool CanMarkPosted(string? state) =>
        IsPendingLike(state) || IsFailed(state) || IsReconciliationRequired(state);

    public static bool CanMarkPending(string? state) => HasBeenSentToTally(state);

    public static bool TallyDivergesIfDeleted(string? state) => HasBeenSentToTally(state);

    // A push is neither in flight nor waiting for the operator to check Tally.
    private static bool HasNoOpenTallyOutcome(string? state) =>
        IsKnown(state) && !IsPosting(state) && !IsReconciliationRequired(state);

    // Tally answered, or may have: accepted, rejected, or outcome unknown.
    private static bool HasBeenSentToTally(string? state) =>
        IsPosted(state) || IsFailed(state) || IsReconciliationRequired(state);

    private static bool IsState(string? state, string target) =>
        string.Equals(state, target, StringComparison.Ordinal);
}
