using System.Text.Json;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal static class BillAuditTrailMapper
{
    public static BillAuditTrailItemViewModel Map(BillAuditEventDto audit)
    {
        var title = audit.EventType switch
        {
            "bill.pending.created" => "Bill saved",
            "bill.pending.updated" => "Bill updated",
            "bill.push.requested" => "Push requested",
            "bill.revised" => "Revision created",
            "bill.voided" => "Bill voided",
            "tally.retry.requested" => "Retry requested",
            "tally.repost.requested" => "Repost requested",
            _ when audit.EventType.StartsWith("tally.", StringComparison.OrdinalIgnoreCase) => HumanizeEvent(audit.EventType),
            _ => HumanizeEvent(audit.EventType)
        };

        var detail = BuildAuditDetail(audit);
        var tone = audit.EventType switch
        {
            "bill.pending.created" or "bill.pending.updated" => "info",
            "bill.push.requested" or "tally.retry.requested" or "tally.repost.requested" => "warn",
            "bill.voided" => "err",
            _ when audit.EventType.Contains("failed", StringComparison.OrdinalIgnoreCase) => "err",
            _ when audit.EventType.Contains("posted", StringComparison.OrdinalIgnoreCase) => "ok",
            _ => "info"
        };

        return new BillAuditTrailItemViewModel(audit.Id, audit.CreatedAtUtc, title, detail, tone, audit);
    }

    private static string BuildAuditDetail(BillAuditEventDto audit)
    {
        var errorMessage = TryGetNestedString(audit.Payload, "details", "errorMessage");
        var errorCode = TryGetNestedString(audit.Payload, "details", "errorCode");
        if (!string.IsNullOrWhiteSpace(errorMessage) || !string.IsNullOrWhiteSpace(errorCode))
        {
            if (!string.IsNullOrWhiteSpace(errorMessage) && !string.IsNullOrWhiteSpace(errorCode))
            {
                return $"{errorCode}: {errorMessage}";
            }
            return errorMessage ?? errorCode!;
        }

        if (TryGetNestedString(audit.Payload, "details", "reason") is { Length: > 0 } reason)
        {
            return reason;
        }

        if (TryGetNestedString(audit.Payload, "details", "invoiceNumber") is { Length: > 0 } invoiceNumber)
        {
            return $"Invoice {invoiceNumber}";
        }

        if (TryGetNestedString(audit.Payload, "details", "fiscalYear") is { Length: > 0 } fiscalYear)
        {
            return $"FY {fiscalYear}";
        }

        if (TryGetString(audit.Payload, "state") is { Length: > 0 } state)
        {
            return $"State: {state}";
        }

        return "System event";
    }

    private static string HumanizeEvent(string eventType)
    {
        var parts = eventType
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]);
        return string.Join(" ", parts);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        return null;
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var parent)
            && parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(nestedPropertyName, out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        return null;
    }
}
