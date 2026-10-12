using System;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// A whole number in a text box the way the showroom writes it, with lakh grouping: 10,00,000. What is
/// typed back may have its commas, spaces and rupee sign left in; only the digits are read. Bind it with
/// the default (lost focus) update, so the text is not regrouped under the caret while it is typed.
/// </summary>
public sealed class GroupedNumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long number => UiCulture.Rupees(number),
            int number => UiCulture.Rupees(number),
            decimal number => UiCulture.Rupees(number),
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var digits = new string((value as string ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return Binding.DoNothing;
        }

        return targetType == typeof(int) ? (object)(int)Math.Min(number, int.MaxValue) : number;
    }
}
