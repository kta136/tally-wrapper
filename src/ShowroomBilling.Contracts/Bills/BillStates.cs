namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// Canonical bill-state strings. Shared by Application, Infrastructure, and Desktop —
/// anything comparing <c>Bill.State</c> to a literal should reference these constants
/// instead so a typo can't silently break workflow sort, filters, or state gates.
///
/// These values are persisted in <c>bills.State</c> (nvarchar) and serialized into
/// contracts; changing a value is a DB migration, not a code change.
/// </summary>
public static class BillStates
{
    public const string Draft = "draft";
    public const string Pending = "pending";
    public const string Posting = "posting";
    public const string Posted = "posted";
    public const string Failed = "failed";
    public const string Revised = "revised";
    public const string Voided = "voided";

    /// <summary>
    /// States that represent unfinished work an operator can push — <c>pending</c>
    /// plus the legacy <c>draft</c> alias. Keep these together in every filter /
    /// sort that treats "not yet sent to Tally" as one bucket.
    /// </summary>
    public static readonly string[] OpenForPush = [Pending, Draft];
}
