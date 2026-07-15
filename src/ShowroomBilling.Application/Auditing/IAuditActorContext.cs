namespace ShowroomBilling.Application.Auditing;

public sealed record AuditActor(string ActorType, string? ActorId);

public interface IAuditActorContext
{
    AuditActor Current { get; }
}
