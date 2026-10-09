namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class InvoiceSequenceEntity
{
    public Guid ShowroomId { get; set; }

    public string FiscalYear { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public long NextValue { get; set; }

    /// <summary>
    /// Rollback never moves <see cref="NextValue"/> below this. Raised when a
    /// bill whose voucher Tally holds is deleted or renumbered, because Tally
    /// still has that number and handing it out again would duplicate it.
    /// </summary>
    public long MinNextValue { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
