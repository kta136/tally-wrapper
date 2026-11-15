using System;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// A bill state as the chips word it ("Reconcile" for <c>reconciliation_required</c>), for a list
/// that offers states to choose from. Anything that is not a state, such as "All", is left as it is.
/// </summary>
public sealed class BillStateLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BillStateDisplay.Label(value as string, editedAfterPush: false);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
