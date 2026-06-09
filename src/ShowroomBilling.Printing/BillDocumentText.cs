using System.Globalization;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Printing;

internal static class BillDocumentText
{
    // CGST/SGST percentages printed in the GST breakup box. V1 hardcoded 1.5/1.5 in
    // defaults.py (CGST_RATE/SGST_RATE); we mirror that until they become configurable.
    internal const decimal CgstRatePct = 1.5m;
    internal const decimal SgstRatePct = 1.5m;
    internal const string DefaultHsnCode = "711319";

    private static readonly CultureInfo IndianCulture = new("en-IN");

    internal static string FormatMoney(decimal value, int decimals = 2)
    {
        var fmt = decimals > 0 && value % 1 == 0m ? "N0" : "N" + decimals;
        return "₹" + value.ToString(fmt, IndianCulture);
    }

    internal static string FormatWeight(decimal value, string unit)
    {
        var num = value % 1 == 0m
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.000", CultureInfo.InvariantCulture);
        return $"{num}{unit}";
    }

    internal static string FormatSigned(decimal value)
    {
        if (value < 0m) return $"− {FormatMoney(-value)}";
        if (value > 0m) return $"+ {FormatMoney(value)}";
        return FormatMoney(0m);
    }

    internal static string FormatMaking(BillLineItemDto line)
    {
        if (IsDiamond(line)) return "—";

        var parts = new List<string>(2);
        var wastage = line.WastagePercent ?? 0m;
        var labour = line.LabourPerUnit ?? 0m;
        if (wastage != 0m)
            parts.Add((wastage % 1 == 0m ? wastage.ToString("0") : wastage.ToString("0.##")) + "%");
        if (labour != 0m) parts.Add($"{FormatCompactNumber(labour)}/g");
        return parts.Count == 0 ? "—" : string.Join("+", parts);
    }

    private static string FormatCompactNumber(decimal value)
    {
        var fmt = value % 1 == 0m ? "N0" : "N2";
        return value.ToString(fmt, IndianCulture);
    }

    internal static bool IsDiamond(BillLineItemDto line)
        => string.Equals(line.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Karat-master lookup mirroring V1 services/print_manager._mapped_tally_item_name:
    /// for non-diamond lines, return the single Tally stock-item name that maps to this
    /// line's karat label. Returns null if the line is diamond, no mapping is supplied,
    /// no entries match the label, or multiple distinct mappings collide.
    /// </summary>
    internal static string? MapTallyItemName(BillLineItemDto line, IReadOnlyList<KaratMasterEntry>? karatMappings)
    {
        if (IsDiamond(line)) return null;
        if (karatMappings is null || karatMappings.Count == 0) return null;

        var label = (line.Karat ?? string.Empty).Trim();
        if (label.Length == 0) return null;

        string? sole = null;
        foreach (var entry in karatMappings)
        {
            var entryLabel = (entry.Label ?? string.Empty).Trim();
            var entryItem = (entry.TallyItem ?? string.Empty).Trim();
            if (entryLabel.Length == 0 || entryItem.Length == 0) continue;
            if (!string.Equals(entryLabel, label, StringComparison.OrdinalIgnoreCase)) continue;

            if (sole is null)
            {
                sole = entryItem;
            }
            else if (!string.Equals(sole, entryItem, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return sole;
    }
}
