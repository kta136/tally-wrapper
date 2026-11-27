using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// An amount of rupees in a text box: lakh grouping, and paise only when it has some (23,200.5). What is
/// typed back may keep its commas and rupee sign; an empty box is none. Text that is no amount is a fault
/// on the box, and the amount stays as it was.
/// <para>
/// Bind it with the default (lost focus) update. With a keystroke update the text is written again from
/// the amount after every key, so the dot of "50." goes before the paise are typed and 50.25 becomes 5,025.
/// </para>
/// </summary>
public sealed class MoneyTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is decimal amount ? amount.ToString("#,##0.##", culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? string.Empty).Replace("₹", string.Empty, StringComparison.Ordinal).Trim();
        if (text.Length == 0)
        {
            return 0m;
        }

        return decimal.TryParse(text, NumberStyles.Number, culture, out var amount)
            ? amount
            : DependencyProperty.UnsetValue;
    }
}
