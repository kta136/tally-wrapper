using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// True when the bound value is null (or the binding found nothing). A style trigger uses it to
/// show one thing while a value is missing and another once it is there, which XAML cannot test
/// for on its own.
/// </summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public static NullToBoolConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || ReferenceEquals(value, DependencyProperty.UnsetValue);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
