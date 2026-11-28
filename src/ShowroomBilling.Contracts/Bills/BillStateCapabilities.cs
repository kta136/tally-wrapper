namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// Shared bill-state rules for the Desktop and server workflows.
/// </summary>
public static class BillStateCapabilities
{
    public static IReadOnlyList<string> PushOrRetryStates { get; } = [BillStates.Pending, BillStates.Failed];

    public static bool IsKnown(string? state) =>
        IsPending(state)
        || IsPosting(state)
        || IsPosted(state)
        || IsFailed(state)
        || IsReconciliationRequired(state)
        || IsRevised(state)
        || IsVoided(state);

    public static bool IsPending(string? state) =>
        IsState(state, BillStates.Pending);

    private static bool IsPosting(string? state) => IsState(state, BillStates.Posting);

    public static bool IsPosted(string? state) => IsState(state, BillStates.Posted);

    public static bool IsFailed(string? state) => IsState(state, BillStates.Failed);

    private static bool IsReconciliationRequired(string? state) =>
        IsState(state, BillStates.ReconciliationRequired);

    private static bool IsRevised(string? state) => IsState(state, BillStates.Revised);

    private static bool IsVoided(string? state) => IsState(state, BillStates.Voided);

    public static bool CanPush(string? state) => IsPending(state);

    public static bool CanRetry(string? state) => IsFailed(state);

    public static bool CanRepost(string? state) => IsPosted(state) || IsFailed(state);

    public static bool CanRevise(string? state) => IsPending(state);

    public static bool CanVoid(string? state) => IsPending(state) || IsFailed(state);

    public static bool CanEdit(string? state) => IsPending(state) || IsFailed(state) || IsPosted(state);

    public static bool CanChangeNumber(string? state) => HasNoOpenTallyOutcome(state);

    public static bool CanDelete(string? state) => HasNoOpenTallyOutcome(state);

    public static bool CanMarkPosted(string? state) =>
        IsPending(state) || IsFailed(state) || IsReconciliationRequired(state);

    public static bool CanMarkPending(string? state) => HasBeenSentToTally(state);

    public static bool TallyDivergesIfDeleted(string? state) => HasBeenSentToTally(state);

    // A push is neither in flight nor waiting for the operator to check Tally.
    private static bool HasNoOpenTallyOutcome(string? state) =>
        IsKnown(state) && !HasOpenTallyOutcome(state);

    public static bool HasOpenTallyOutcome(string? state) => IsPosting(state) || IsReconciliationRequired(state);

    // Tally answered, or may have: accepted, rejected, or outcome unknown.
    private static bool HasBeenSentToTally(string? state) =>
        IsPosted(state) || IsFailed(state) || IsReconciliationRequired(state);

    private static bool IsState(string? state, string target) =>
        string.Equals(state, target, StringComparison.Ordinal);
}
