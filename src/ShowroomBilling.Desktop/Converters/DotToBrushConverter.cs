using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ShowroomBilling.Desktop.Converters;

public sealed class DotToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "ok" => "OkBrush",
            "warn" => "WarnBrush",
            "err" => "ErrBrush",
            _ => "InkDisabledBrush",
        };
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

