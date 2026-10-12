using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>One row of the line-item table of the Bill Details dialog, formatted as the operator reads it.</summary>
public sealed record BillDetailsLine(string ItemName, string Net, string Karat, string Rate, string LineTotal);

/// <summary>
/// One entry of the timeline: an audit event, or the step the bill is waiting for. <paramref name="Tone"/> colours
/// the bullet; a pending entry is drawn as a ring, and the line between bullets stops at the last one.
/// </summary>
public sealed record TimelineEntry(string Title, string Detail, string Time, string Tone, bool IsFirst, bool IsLast, bool IsPending);

/// <summary>What the Bill Details dialog shows, worked out for display: amounts with lakh grouping, weights with their unit, the timeline and the one-sentence state.</summary>
public partial class BillDetailsViewModel
{
    private static string Amount(decimal? value) => value is { } amount ? UiCulture.Amount(amount) : "—";

    public IReadOnlyList<BillDetailsLine> LineRows => Lines.Select(line => new BillDetailsLine(
        line.ItemName,
        $"{UiCulture.Weight(line.Quantity)} {UnitWord(line.Unit)}".TrimEnd(),
        string.IsNullOrWhiteSpace(line.Karat) ? "—" : line.Karat!,
        UiCulture.Amount(line.Rate),
        UiCulture.Amount(line.LineTotal))).ToArray();

    private static string UnitWord(string? unit) => unit switch
    {
        ItemUnits.Gram => "g",
        null => string.Empty,
        _ => unit,
    };

    public string PartyDisplay => string.IsNullOrWhiteSpace(Payload?.PartyName) ? "Walk-in Customer" : Payload!.PartyName!;

    public string PaymentDisplay => string.IsNullOrWhiteSpace(Payload?.Payment) ? "—" : Payload!.Payment!;

    public string Rate24KtDisplay => Payload?.Rate24Kt is { } rate
        ? $"₹{(rate % 1 == 0 ? UiCulture.Rupees(rate) : UiCulture.Amount(rate))} per gram"
        : "—";

    public string SubtotalDisplay => Amount(Totals?.Subtotal);

    public string TaxDisplay => Amount(Totals?.TaxTotal);

    public bool HasDiscount => Totals is { DiscountTotal: > 0 };

    public string DiscountDisplay => Totals is { DiscountTotal: > 0 } totals ? "−" + UiCulture.Amount(totals.DiscountTotal) : Amount(Totals?.DiscountTotal);

    public string RoundOffDisplay => Amount(Totals?.RoundOff);

    public string GrandTotalDisplay => Totals is { } totals ? "₹" + UiCulture.Rupees(totals.GrandTotal) : "—";

    /// <summary>What state the bill is in, in a sentence, for the foot of the dialog.</summary>
    public string StateNote => Bill?.State switch
    {
        BillStates.Draft => "Draft. Nothing has been sent to Tally.",
        BillStates.Pending => "Pending. Nothing has been sent to Tally yet.",
        BillStates.Posting => "Posting. Waiting for Tally to answer.",
        BillStates.Posted => "Posted to Tally.",
        BillStates.Failed => "Push failed. The timeline has the reason.",
        BillStates.ReconciliationRequired => "Check the voucher in Tally before another write.",
        BillStates.Voided => "Voided. It stays in the list and cannot be sent.",
        BillStates.Revised => "Revised. A newer bill replaces this one.",
        _ => string.Empty,
    };

    /// <summary>The foot of the dialog says what the last action did; until there is one, the state of the bill.</summary>
    public string FooterText => string.IsNullOrWhiteSpace(StatusMessage) ? StateNote : StatusMessage;

    /// <summary>The audit events, then the step the bill is waiting for when it is waiting for one.</summary>
    public IReadOnlyList<TimelineEntry> Timeline
    {
        get
        {
            var next = NextStep;
            var entries = new List<TimelineEntry>();
            for (var i = 0; i < AuditTrail.Count; i++)
            {
                var item = AuditTrail[i];
                var local = item.CreatedAtUtc.ToLocalTime();
                var time = local.Date == DateTime.Today
                    ? local.ToString("HH:mm", UiCulture.Culture)
                    : local.ToString("dd MMM, HH:mm", UiCulture.Culture);
                entries.Add(new TimelineEntry(
                    item.Title, item.Detail, time, item.Tone,
                    IsFirst: i == 0,
                    IsLast: i == AuditTrail.Count - 1 && next is null,
                    IsPending: false));
            }

            if (next is { } step)
            {
                entries.Add(new TimelineEntry(step.Title, step.Detail, string.Empty, "neutral", IsFirst: entries.Count == 0, IsLast: true, IsPending: true));
            }

            return entries;
        }
    }

    private (string Title, string Detail)? NextStep => Bill?.State switch
    {
        BillStates.Draft or BillStates.Pending
            => ("Waiting for Tally", "Nothing has been sent yet. Push it from here or from the Bills tab."),
        BillStates.Failed
            => ("Waiting for a retry", "Tally did not take the voucher. Retry it once the cause is fixed."),
        BillStates.ReconciliationRequired
            => ("Check Tally", "Tally may already hold the voucher. Look for it there, then mark the bill as pushed or pending."),
        _ => null,
    };

    private void NotifyDisplayChanged()
    {
        OnPropertyChanged(nameof(LineRows));
        OnPropertyChanged(nameof(PartyDisplay));
        OnPropertyChanged(nameof(PaymentDisplay));
        OnPropertyChanged(nameof(Rate24KtDisplay));
        OnPropertyChanged(nameof(SubtotalDisplay));
        OnPropertyChanged(nameof(TaxDisplay));
        OnPropertyChanged(nameof(HasDiscount));
        OnPropertyChanged(nameof(DiscountDisplay));
        OnPropertyChanged(nameof(RoundOffDisplay));
        OnPropertyChanged(nameof(GrandTotalDisplay));
        OnPropertyChanged(nameof(StateNote));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(Timeline));
    }

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(FooterText));
}
