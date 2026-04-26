namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class AdminPasscodeEntity
{
    public Guid Id { get; set; }

    public Guid ShowroomId { get; set; }

    public string PasscodeHash { get; set; } = string.Empty;

    public string PasscodeSalt { get; set; } = string.Empty;

    public int Iterations { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
