using System.Text.Json;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal static class BillAuditTrailMapper
{
    public static BillAuditTrailItemViewModel Map(BillAuditEventDto audit)
    {
        var title = TitleFor(audit.EventType) ?? HumanizeEvent(audit.EventType);
        var detail = BuildAuditDetail(audit);
        var tone = audit.EventType switch
        {
            BillAuditEvents.TallyPosted or BillAuditEvents.MarkPosted => "ok",
            BillAuditEvents.TallyFailed or BillAuditEvents.Voided or BillAuditEvents.Deleted => "err",
            BillAuditEvents.PushRequested
                or BillAuditEvents.TallyOutcomeUnknown
                or BillAuditEvents.PostingRecovered => "warn",
            _ => "info"
        };

        return new BillAuditTrailItemViewModel(audit.Id, audit.CreatedAtUtc, title, detail, tone, audit);
    }

    /// <summary>
    /// The operator-facing title for a server event, or null for one this
    /// build does not know. Every name in <see cref="BillAuditEvents"/> has a
    /// title; a test checks that, so a new server event cannot ship untitled.
    /// </summary>
    internal static string? TitleFor(string eventType) => eventType switch
    {
        BillAuditEvents.PendingCreated => "Bill saved",
        BillAuditEvents.PendingUpdated => "Bill updated",
        BillAuditEvents.Revised => "Revision created",
        BillAuditEvents.Voided => "Bill voided",
        BillAuditEvents.EditReopened => "Reopened for editing",
        BillAuditEvents.NumberChanged => "Invoice number changed",
        BillAuditEvents.MarkPosted => "Marked as pushed",
        BillAuditEvents.MarkPending => "Marked as pending",
        BillAuditEvents.Deleted => "Bill deleted",
        BillAuditEvents.PushRequested => "Push requested",
        BillAuditEvents.PushCancelled => "Push cancelled",
        BillAuditEvents.PostingRecovered => "Stuck push recovered",
        BillAuditEvents.TallyPosted => "Posted to Tally",
        BillAuditEvents.TallyFailed => "Tally rejected the voucher",
        BillAuditEvents.TallyOutcomeUnknown => "Tally outcome unknown",
        _ => null
    };

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

        // Recovery rows written before the shared payload shape keep the reason at the root.
        if ((TryGetNestedString(audit.Payload, "details", "reason") ?? TryGetString(audit.Payload, "reason"))
            is { Length: > 0 } reason)
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
