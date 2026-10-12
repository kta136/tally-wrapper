using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// The word that follows a count, in the right number: with the parameter <c>" bill| bills"</c>, 1 gives " bill"
/// and anything else " bills".
/// </summary>
public sealed class CountWordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var words = (parameter as string ?? string.Empty).Split('|');
        var count = value switch
        {
            int number => number,
            long number => (int)number,
            _ => 0,
        };
        return count == 1 || words.Length < 2 ? words[0] : words[1];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
