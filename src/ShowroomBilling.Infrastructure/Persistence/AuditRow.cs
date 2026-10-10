using System.Text.Json;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Persistence;

/// <summary>
/// Builds audit rows. Payloads are always serialized from an object, never
/// assembled as text: a quote in a reason or an invoice number would break
/// hand-built JSON without any error.
/// </summary>
internal static class AuditRow
{
    /// <summary>Actor for work no request asked for: start-up recovery, number allocation.</summary>
    internal static readonly AuditActor System = new("system", null);

    /// <param name="payload">
    /// An object to serialize, an already-serialized JSON string, or null for an empty payload.
    /// </param>
    internal static AuditEventEntity Create(
        string entityType,
        string entityId,
        string eventType,
        AuditActor actor,
        DateTimeOffset at,
        object? payload = null) => new()
        {
            Id = Guid.CreateVersion7(),
            EntityType = entityType,
            EntityId = entityId,
            EventType = eventType,
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            PayloadJson = payload switch
            {
                null => "{}",
                string json => json,
                _ => JsonSerializer.Serialize(payload),
            },
            CreatedAtUtc = at,
        };
}
