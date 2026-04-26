using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

public sealed record BillAuditTrailItemViewModel(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    string Title,
    string Detail,
    string Tone,
    BillAuditEventDto Source)
{
    public string TimeText => CreatedAtUtc.ToLocalTime().ToString("dd MMM HH:mm");
}
