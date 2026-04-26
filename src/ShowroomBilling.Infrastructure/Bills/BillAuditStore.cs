using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

internal sealed class BillAuditStore(ShowroomBillingDbContext dbContext)
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
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "bill",
            EntityId = billId.ToString(),
            EventType = eventType,
            ActorType = "system",
            PayloadJson = payload,
            CreatedAtUtc = at
        });
    }

    internal async Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        var billExists = await dbContext.Bills.AsNoTracking()
            .AnyAsync(b => b.Id == billId, cancellationToken);
        if (!billExists) return null;

        var billIdText = billId.ToString();
        var raw = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill" && e.EntityId == billIdText)
            .OrderBy(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

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
        var lastFailQuery = dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill" && e.EntityId == billIdText && e.EventType == "tally.failed")
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(1);
        var lastSuccessQuery = dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill" && e.EntityId == billIdText && e.EventType == "tally.posted")
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(1);
        var combined = await lastFailQuery.Concat(lastSuccessQuery).ToListAsync(cancellationToken);
        var lastFail = combined.FirstOrDefault(e => e.EventType == "tally.failed");
        var lastSuccess = combined.FirstOrDefault(e => e.EventType == "tally.posted");

        string? lastErrorCode = null, lastErrorMessage = null, lastRemoteId = null;
        if (lastFail is not null)
        {
            lastErrorCode = ReadDetailsString(lastFail.PayloadJson, "errorCode");
            lastErrorMessage = ReadDetailsString(lastFail.PayloadJson, "errorMessage");
        }
        if (lastSuccess is not null)
        {
            lastRemoteId = ReadDetailsString(lastSuccess.PayloadJson, "remoteId");
        }

        return new BillPostingStatusResponse(
            BillId: billId,
            BillState: bill.State,
            ActiveJobId: null,
            JobState: null,
            AttemptCount: 0,
            LastErrorCode: lastErrorCode,
            LastErrorMessage: lastErrorMessage,
            LastRemoteId: lastRemoteId,
            NextAttemptAtUtc: null);
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
