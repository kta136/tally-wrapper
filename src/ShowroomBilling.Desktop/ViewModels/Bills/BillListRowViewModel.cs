using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Bills;

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

    public bool IsPendingLike => State is "pending" or "draft";
    public bool IsRetryable => State == "failed";

    public bool CanBePushed => IsPendingLike;
    public bool CanBeRetried => IsRetryable;
    public bool CanBeReposted => State is "posted" or "failed";
    public bool CanBeRevised => State is "pending" or "draft" or "posted";
    public bool CanBeVoided => State is "pending" or "draft" or "failed";
    public bool CanBeEdited => State is "pending" or "draft" or "failed" or "posted";
    public bool CanChangeNumber => State != "posting";
    public bool CanBeDeleted => State != "posting";
    public bool CanMarkPosted => State is "pending" or "draft" or "failed";
    public bool CanMarkPending => State is "posted" or "failed";
    public bool CanCopyInvoiceNumber => !string.IsNullOrWhiteSpace(InvoiceNumber);

    public string RowNote => State switch
    {
        "pending" or "draft" => "Awaiting Tally push",
        "posting" => "Posting in progress",
        "posted" => EditedAfterPush ? "Edited after push" : "—",
        "failed" => "Requires retry or repost",
        "voided" => "Voided locally",
        "revised" => "Revision created",
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
                "pending" => "◐ Pending",
                "draft" => "○ Draft",
                "posting" => "◐ Posting",
                "posted" => "● Posted",
                "failed" => "✕ Failed",
                "revised" => "↻ Revised",
                "voided" => "— Voided",
                _ => State
            };
            return EditedAfterPush ? $"{basic} · edit" : basic;
        }
    }
}
