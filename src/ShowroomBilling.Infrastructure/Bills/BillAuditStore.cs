using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillAuditStore(
    ShowroomBillingDbContext dbContext,
    IAuditActorContext? actorContext = null)
{
    internal void Write(
        Guid billId,
        string eventType,
        string state,
        DateTimeOffset at,
        object? details = null)
    {
        var payload = details is null
            ? JsonSerializer.Serialize(new { state }, BillSerialization.JsonOptions)
            : JsonSerializer.Serialize(new { state, details }, BillSerialization.JsonOptions);
        var actor = actorContext?.Current ?? new AuditActor("system", null);
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            EntityType = "bill",
            EntityId = billId.ToString(),
            EventType = eventType,
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            PayloadJson = payload,
            CreatedAtUtc = at
        });
    }

    internal async Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var billIdText = billId.ToString();
        var raw = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill" && e.EntityId == billIdText)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
        if (raw.Count == 0) return null;

        var events = raw.Select(e => new BillAuditEventDto(
            e.Id,
            e.EventType,
            e.ActorType,
            e.ActorId,
            e.EntityType,
            e.EntityId,
            ParsePayload(e.PayloadJson),
            e.CreatedAtUtc)).ToList();

        return new BillAuditResponse(billId, events);
    }

    internal async Task<BillPostingStatusResponse?> GetPostingStatusAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
    {
        var bill = await dbContext.Bills.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billId, cancellationToken);
        if (bill is null) return null;

        // Single round-trip for both audit lookups. Each side of the Concat picks
        // its own most-recent row using the covering
        // (EntityType, EntityId, EventType, CreatedAtUtc DESC) index from
        // PerformanceReadOptimizations, so the union returns at most two rows.
        var billIdText = billId.ToString();
        var lastProblemQuery = dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && (e.EventType == "tally.failed" || e.EventType == "tally.outcome.unknown"))
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(1);
        var lastSuccessQuery = dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill" && e.EntityId == billIdText && e.EventType == "tally.posted")
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(1);
        var combined = await lastProblemQuery.Concat(lastSuccessQuery).ToListAsync(cancellationToken);
        var lastProblem = combined.FirstOrDefault(e =>
            e.EventType is "tally.failed" or "tally.outcome.unknown");
        var lastSuccess = combined.FirstOrDefault(e => e.EventType == "tally.posted");

        string? lastErrorCode = null, lastErrorMessage = null, lastRemoteId = null;
        if (lastProblem is not null)
        {
            lastErrorCode = ReadDetailsString(lastProblem.PayloadJson, "errorCode");
            lastErrorMessage = ReadDetailsString(lastProblem.PayloadJson, "errorMessage");
        }
        if (lastSuccess is not null)
        {
            lastRemoteId = ReadDetailsString(lastSuccess.PayloadJson, "remoteId");
        }

        return new BillPostingStatusResponse(
            BillId: billId,
            BillState: bill.State,
            LastErrorCode: lastErrorCode,
            LastErrorMessage: lastErrorMessage,
            LastRemoteId: lastRemoteId);
    }

    private static string? ReadDetailsString(string? payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && details.TryGetProperty(propertyName, out var v)
                && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var wrapped = JsonDocument.Parse(JsonSerializer.Serialize(new { raw = payloadJson }));
            return wrapped.RootElement.Clone();
        }
    }
}
