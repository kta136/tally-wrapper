using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// True when the bound <c>decimal?</c> is greater than zero. The Invoice line
/// table uses it in a <c>DataTrigger</c> to tint a cell that holds an active
/// deduction (Less Wt) or rate (Diamond Rate).
/// </summary>
public sealed class DecimalIsPositiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is decimal amount && amount > 0m;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
