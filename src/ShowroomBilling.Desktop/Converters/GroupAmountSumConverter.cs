using System;
using System.Collections;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Sums <see cref="BillListRowViewModel.GrandTotal"/> across the items of a
/// <see cref="System.Windows.Data.CollectionViewGroup"/> for the Bills date-group header.
/// Returns the sum formatted with a leading "₹" (e.g. "₹1,23,400").
/// </summary>
public sealed class GroupAmountSumConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable items)
        {
            return string.Empty;
        }

        decimal sum = 0m;
        foreach (var entry in items)
        {
            if (entry is BillListRowViewModel row)
            {
                sum += row.GrandTotal;
            }
        }

        return "₹" + sum.ToString("N0", culture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
