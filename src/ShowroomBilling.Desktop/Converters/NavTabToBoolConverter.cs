using System;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Converters;

public sealed class NavTabToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NavTab tab && parameter is NavTab expected)
            return tab == expected;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is NavTab expected)
            return expected;
        return Binding.DoNothing;
    }
}
