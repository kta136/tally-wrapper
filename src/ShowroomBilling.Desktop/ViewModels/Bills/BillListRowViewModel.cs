using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

public partial class BillListRowViewModel(BillSummaryItem item) : ObservableObject
{
    public BillSummaryItem Item { get; } = item;

    [ObservableProperty] private bool isSelected;

    public Guid Id => Item.Id;
    public string State => Item.State;
    public string? InvoiceNumber => Item.InvoiceNumber;
    public string InvoiceNumberDisplay => string.IsNullOrWhiteSpace(Item.InvoiceNumber) ? "(pending)" : Item.InvoiceNumber!;
    public string PartyName => string.IsNullOrWhiteSpace(Item.PartyName) ? "Walk-in Customer" : Item.PartyName!;
    public DateOnly? BillDate => Item.BillDate;
    public decimal GrandTotal => Item.GrandTotal;
    public DateTimeOffset CreatedAtUtc => Item.CreatedAtUtc;
    public DateTimeOffset UpdatedAtUtc => Item.UpdatedAtUtc;
    public bool EditedAfterPush => Item.EditedAfterPush;

    /// <summary>
    /// The number Tally filed the bill's voucher under, when that is not the bill's own number
    /// (with Automatic numbering Tally gives its own); null otherwise, or when it is not known.
    /// </summary>
    public string? TallyNumberWhenDifferent =>
        !string.IsNullOrWhiteSpace(Item.TallyVoucherNumber)
        && !string.Equals(Item.TallyVoucherNumber, Item.InvoiceNumber, StringComparison.Ordinal)
            ? Item.TallyVoucherNumber
            : null;

    // Numeric sort key parsed from the trailing digits of InvoiceNumber so the
    // Bills list orders bills naturally (93 above 92) instead of by CreatedAt
    // (which floats a just-edited bill above peers with a higher number). Bills
    // without an invoice number yet (pure drafts) sort to 0 so they fall below
    // numbered peers within the same day.
    public long InvoiceNumberSortKey => InvoiceNumberFormatter.TryParseTrailingCore(Item.InvoiceNumber) ?? 0L;

    public bool IsPending => BillStateCapabilities.IsPending(State);
    public bool IsRetryable => BillStateCapabilities.CanRetry(State);

    public bool CanBePushed => BillStateCapabilities.CanPush(State);
    public bool CanBeRetried => BillStateCapabilities.CanRetry(State);
    public bool CanBeReposted => BillStateCapabilities.CanRepost(State);
    public bool CanBeEdited => BillStateCapabilities.CanEdit(State);
    public bool CanChangeNumber => BillStateCapabilities.CanChangeNumber(State);
    public bool CanBeDeleted => BillStateCapabilities.CanDelete(State);
    public bool CanMarkPosted => BillStateCapabilities.CanMarkPosted(State);
    public bool CanMarkPending => BillStateCapabilities.CanMarkPending(State);
    public bool CanCopyInvoiceNumber => !string.IsNullOrWhiteSpace(InvoiceNumber);

    public string RowNote => State switch
    {
        // An edit of a pushed bill alters the voucher Tally filed it under.
        BillStates.Pending when EditedAfterPush && TallyNumberWhenDifferent is { } held => $"Awaiting push · in Tally as {held}",
        BillStates.Pending => "Awaiting Tally push",
        BillStates.Posting => "Posting in progress",
        BillStates.Posted when TallyNumberWhenDifferent is { } inTally =>
            EditedAfterPush ? $"Edited after push · in Tally as {inTally}" : $"In Tally as {inTally}",
        BillStates.Posted => EditedAfterPush ? "Edited after push" : "—",
        BillStates.Failed => "Push failed. Open details for the reason",
        BillStates.ReconciliationRequired => "Verify the voucher in Tally before another write",
        BillStates.Voided => "Voided locally",
        BillStates.Revised => "Revision created",
        _ => "—"
    };

    /// <summary>
    /// "err" when the note says something has to be done before the bill can move on; the list draws
    /// it in red. "warn" when Tally holds the voucher under another number, drawn in amber.
    /// </summary>
    public string NoteTone => BillStateDisplay.Tone(State) == BillStateDisplay.ToneErr ? BillStateDisplay.ToneErr
        : TallyNumberWhenDifferent is not null && (State == BillStates.Posted || State == BillStates.Pending && EditedAfterPush)
            ? BillStateDisplay.ToneWarn
        : BillStateDisplay.ToneNeutral;

    public bool IsVoided => State == BillStates.Voided;

    public string StateChipLabel => BillStateDisplay.Label(State, EditedAfterPush);

    /// <summary>The shape drawn before the state's word; see <see cref="BillStateDisplay.Kind"/>.</summary>
    public string StateKind => BillStateDisplay.Kind(State);

    /// <summary>Colour tone of the state chip; see <see cref="BillStateDisplay"/>.</summary>
    public string StateTone => BillStateDisplay.Tone(State);
}
