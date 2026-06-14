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
        || IsRevised(state)
        || IsVoided(state);

    public static bool IsPendingLike(string? state) =>
        IsState(state, BillStates.Pending) || IsState(state, BillStates.Draft);

    public static bool IsPosting(string? state) => IsState(state, BillStates.Posting);

    public static bool IsPosted(string? state) => IsState(state, BillStates.Posted);

    public static bool IsFailed(string? state) => IsState(state, BillStates.Failed);

    public static bool IsRevised(string? state) => IsState(state, BillStates.Revised);

    public static bool IsVoided(string? state) => IsState(state, BillStates.Voided);

    public static bool CanPush(string? state) => IsPendingLike(state);

    public static bool CanRetry(string? state) => IsFailed(state);

    public static bool CanRepost(string? state) => IsPosted(state) || IsFailed(state);

    public static bool CanRevise(string? state) => IsPendingLike(state) || IsPosted(state);

    public static bool CanVoid(string? state) => IsPendingLike(state) || IsFailed(state);

    public static bool CanEdit(string? state) => IsPendingLike(state) || IsFailed(state) || IsPosted(state);

    public static bool CanChangeNumber(string? state) => IsKnown(state) && !IsPosting(state);

    public static bool CanDelete(string? state) => IsKnown(state) && !IsPosting(state);

    public static bool CanMarkPosted(string? state) => IsPendingLike(state) || IsFailed(state);

    public static bool CanMarkPending(string? state) => IsPosted(state) || IsFailed(state);

    public static bool TallyDivergesIfDeleted(string? state) => IsPosted(state) || IsFailed(state);

    private static bool IsState(string? state, string target) =>
        string.Equals(state, target, StringComparison.Ordinal);
}
