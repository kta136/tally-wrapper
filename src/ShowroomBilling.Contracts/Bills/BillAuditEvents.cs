namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// Event types written to a bill's audit trail. The server both writes and
/// queries these (for example "is this bill in Tally?" looks for the last
/// <see cref="TallyPosted"/>), and the Desktop titles each one in the bill
/// timeline, so every reference goes through these constants.
/// </summary>
public static class BillAuditEvents
{
    public const string PendingCreated = "bill.pending.created";
    public const string PendingUpdated = "bill.pending.updated";
    public const string Revised = "bill.revised";
    public const string Voided = "bill.voided";
    public const string EditReopened = "bill.edit.reopened";
    public const string NumberChanged = "bill.number.changed";
    public const string MarkPosted = "bill.mark_posted";
    public const string MarkPending = "bill.mark_pending";
    public const string Deleted = "bill.deleted";

    public const string PushRequested = "bill.push.requested";
    public const string PushCancelled = "bill.push.cancelled";
    public const string PostingRecovered = "bill.posting.recovered";

    public const string TallyPosted = "tally.posted";
    public const string TallyFailed = "tally.failed";
    public const string TallyOutcomeUnknown = "tally.outcome.unknown";

    /// <summary>Every event type above, for code that must handle them all.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PendingCreated, PendingUpdated, Revised, Voided, EditReopened, NumberChanged,
        MarkPosted, MarkPending, Deleted, PushRequested, PushCancelled, PostingRecovered,
        TallyPosted, TallyFailed, TallyOutcomeUnknown,
    ];
}
