using System;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Turns the stored value of an item-master option into the words the operator reads: the category
/// ("gold_based" is "Gold-based") or the pricing mode ("both" is "Wastage + labour"). The converter
/// parameter says which: <c>category</c> or <c>pricing</c>. An unknown value is shown as it is.
/// </summary>
public sealed class MasterOptionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        return (parameter as string, text) switch
        {
            ("category", ItemCategories.GoldBased) => "Gold-based",
            ("category", ItemCategories.Diamond) => "Diamond",
            ("pricing", PricingModes.Wastage) => "Wastage only",
            ("pricing", PricingModes.Labour) => "Labour only",
            ("pricing", PricingModes.Both) => "Wastage + labour",
            _ => text,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
