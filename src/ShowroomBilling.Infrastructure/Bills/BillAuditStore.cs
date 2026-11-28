using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Bills;

public sealed class BillAuditStore(
    ShowroomBillingDbContext dbContext,
    IAuditActorContext? actorContext = null)
{
    /// <returns>The written row, so a later row can name it and a failed save can let it go.</returns>
    internal AuditEventEntity Write(
        Guid billId,
        string eventType,
        string state,
        DateTimeOffset at,
        object? details = null)
    {
        var actor = actorContext?.Current ?? AuditRow.System;
        var row = AuditRow.Create("bill", billId.ToString(), eventType, actor, at, BuildPayload(state, details));
        dbContext.AuditEvents.Add(row);
        return row;
    }

    /// <summary>The <c>{ state, details }</c> shape every bill event carries; the Desktop reads <c>details</c>.</summary>
    internal static string BuildPayload(string state, object? details) =>
        details is null
            ? JsonSerializer.Serialize(new { state }, BillSerialization.JsonOptions)
            : JsonSerializer.Serialize(new { state, details }, BillSerialization.JsonOptions);

    public async Task<BillAuditResponse?> GetAuditAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
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

    public async Task<BillPostingStatusResponse?> GetPostingStatusAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
    {
        var billState = await dbContext.Bills.AsNoTracking()
            .Where(b => b.Id == billId)
            .Select(b => b.State)
            .FirstOrDefaultAsync(cancellationToken);
        if (billState is null) return null;

        // The most recent push that failed or was left undecided, read through the
        // (EntityType, EntityId, EventType, CreatedAtUtc DESC) index.
        var billIdText = billId.ToString();
        var lastProblem = await dbContext.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == "bill"
                && e.EntityId == billIdText
                && (e.EventType == BillAuditEvents.TallyFailed || e.EventType == BillAuditEvents.TallyOutcomeUnknown))
            .OrderByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        string? lastErrorCode = null, lastErrorMessage = null;
        if (lastProblem is not null)
        {
            lastErrorCode = ReadDetailsString(lastProblem.PayloadJson, "errorCode");
            lastErrorMessage = ReadDetailsString(lastProblem.PayloadJson, "errorMessage");
        }

        return new BillPostingStatusResponse(
            BillId: billId,
            BillState: billState,
            LastErrorCode: lastErrorCode,
            LastErrorMessage: lastErrorMessage);
    }

    /// <summary>Reads a details string. Older payloads can use a legacy property when the current property is absent.</summary>
    internal static string? ReadDetailsString(string? payloadJson, string propertyName, string? legacyProperty = null)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("details", out var details)
                && details.ValueKind == JsonValueKind.Object
                && (details.TryGetProperty(propertyName, out var v)
                    || legacyProperty is not null && details.TryGetProperty(legacyProperty, out v))
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
