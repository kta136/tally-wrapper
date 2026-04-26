using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Returns <c>true</c> (or <see cref="Visibility.Visible"/>) when the bound
/// numeric value is strictly greater than zero. Used by the Invoice line table
/// to colour-shift cells that hold an active non-zero deduction (Less Wt →
/// amber) or rate (Diamond Rate → indigo), and to show the eff-rate row's
/// netWt subtitle only when there's a real value to display.
///
/// Returns the target type's "off" value (false / Collapsed) for null,
/// unparseable, or non-positive inputs.
/// </summary>
public sealed class DecimalIsPositiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var positive = value switch
        {
            null => false,
            decimal d => d > 0m,
            double dbl => dbl > 0d,
            float f => f > 0f,
            long l => l > 0L,
            int i => i > 0,
            string s when decimal.TryParse(s, NumberStyles.Number, culture, out var parsed) => parsed > 0m,
            _ => false,
        };

        if (targetType == typeof(Visibility))
        {
            return positive ? Visibility.Visible : Visibility.Collapsed;
        }
        return positive;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
