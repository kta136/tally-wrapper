namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class InvoiceSequenceEntity
{
    public Guid ShowroomId { get; set; }

    public string FiscalYear { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public long NextValue { get; set; }

    /// <summary>
    /// One past the highest core that Tally holds a voucher for although no
    /// bill here has it any more: raised when a bill whose voucher Tally holds
    /// is deleted or renumbered. Rollback never moves <see cref="NextValue"/>
    /// below this, and a reservation never hands out the core just under it,
    /// because issuing that number again would duplicate it in Tally.
    /// </summary>
    public long MinNextValue { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
