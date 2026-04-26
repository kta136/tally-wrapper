namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class AdminSessionEntity
{
    public Guid Id { get; set; }

    public Guid ShowroomId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public string ActorLabel { get; set; } = string.Empty;

    public DateTimeOffset IssuedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string? RevokedReason { get; set; }
}
