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

    // Numeric sort key parsed from the trailing digits of InvoiceNumber so the
    // Bills list orders bills naturally (93 above 92) instead of by CreatedAt
    // (which floats a just-edited bill above peers with a higher number). Bills
    // without an invoice number yet (pure drafts) sort to 0 so they fall below
    // numbered peers within the same day.
    public long InvoiceNumberSortKey => InvoiceNumberFormatter.TryParseTrailingCore(Item.InvoiceNumber) ?? 0L;

    public bool IsPendingLike => BillStateCapabilities.IsPendingLike(State);
    public bool IsRetryable => BillStateCapabilities.CanRetry(State);

    public bool CanBePushed => BillStateCapabilities.CanPush(State);
    public bool CanBeRetried => BillStateCapabilities.CanRetry(State);
    public bool CanBeReposted => BillStateCapabilities.CanRepost(State);
    public bool CanBeRevised => BillStateCapabilities.CanRevise(State);
    public bool CanBeVoided => BillStateCapabilities.CanVoid(State);
    public bool CanBeEdited => BillStateCapabilities.CanEdit(State);
    public bool CanChangeNumber => BillStateCapabilities.CanChangeNumber(State);
    public bool CanBeDeleted => BillStateCapabilities.CanDelete(State);
    public bool CanMarkPosted => BillStateCapabilities.CanMarkPosted(State);
    public bool CanMarkPending => BillStateCapabilities.CanMarkPending(State);
    public bool CanCopyInvoiceNumber => !string.IsNullOrWhiteSpace(InvoiceNumber);

    public string RowNote => State switch
    {
        BillStates.Pending or BillStates.Draft => "Awaiting Tally push",
        BillStates.Posting => "Posting in progress",
        BillStates.Posted => EditedAfterPush ? "Edited after push" : "—",
        BillStates.Failed => "Requires retry or repost",
        BillStates.ReconciliationRequired => "Verify the voucher in Tally before another write",
        BillStates.Voided => "Voided locally",
        BillStates.Revised => "Revision created",
        _ => "—"
    };

    public string StateChipLabel
    {
        get
        {
            // Leading glyphs match the design bundle's chip iconography
            // (● filled = posted/posting, ◐ half = pending, ○ empty = draft,
            //  ✕ = failed, ↻ = revised, — em-dash = voided). Mono-friendly
            //  characters chosen so they line up next to JetBrains/Cascadia.
            var basic = State switch
            {
                BillStates.Pending => "◐ Pending",
                BillStates.Draft => "○ Draft",
                BillStates.Posting => "◐ Posting",
                BillStates.Posted => "● Posted",
                BillStates.Failed => "✕ Failed",
                BillStates.ReconciliationRequired => "! Reconcile",
                BillStates.Revised => "↻ Revised",
                BillStates.Voided => "— Voided",
                _ => State
            };
            return EditedAfterPush ? $"{basic} · edit" : basic;
        }
    }
}
