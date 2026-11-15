using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// The line under an item's name where the Invoice tab lists items. Under a line's item cell it is
/// "gm · 8% wastage · ₹320/g labour"; with the parameter <c>quick</c>, for the Quick add list, the
/// shorter "8% wastage · ₹320/g".
/// </summary>
public sealed class ItemSummaryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ItemMasterEntry item
            ? Summarise(item, quick: string.Equals(parameter as string, "quick", StringComparison.OrdinalIgnoreCase))
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static string Summarise(ItemMasterEntry item, bool quick)
    {
        var parts = new List<string>();
        if (!quick && !string.IsNullOrWhiteSpace(item.Unit))
        {
            parts.Add(item.Unit);
        }

        if (string.Equals(item.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"flat rate per {(string.IsNullOrWhiteSpace(item.Unit) ? ItemUnits.Carat : item.Unit)}");
            return string.Join(" · ", parts);
        }

        var mode = string.IsNullOrWhiteSpace(item.PricingMode) ? PricingModes.Wastage : item.PricingMode;
        if (mode != PricingModes.Labour)
        {
            parts.Add($"{item.WastagePercent.ToString(CultureInfo.InvariantCulture)}% wastage");
        }

        if (mode == PricingModes.Labour || mode == PricingModes.Both)
        {
            parts.Add($"₹{item.DefaultLabourPerGram.ToString(CultureInfo.InvariantCulture)}/g{(quick ? string.Empty : " labour")}");
        }

        return string.Join(" · ", parts);
    }
}
