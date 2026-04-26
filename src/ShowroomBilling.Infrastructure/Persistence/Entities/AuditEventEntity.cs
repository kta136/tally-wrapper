namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class AuditEventEntity
{
    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string ActorType { get; set; } = string.Empty;

    public string? ActorId { get; set; }

    public string PayloadJson { get; set; } = "{}";

    public DateTimeOffset CreatedAtUtc { get; set; }
}
