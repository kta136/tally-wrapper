using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Formats a <see cref="DateOnly"/>? group key for the Bills list section header
/// (e.g. "Today · Fri, 25 Apr 2026", "Yesterday · Thu, 24 Apr 2026", or just the date).
/// Null bill dates render as "No date".
/// </summary>
public sealed class BillDateGroupHeaderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null)
        {
            return "No date";
        }

        DateOnly date;
        switch (value)
        {
            case DateOnly d:
                date = d;
                break;
            case DateTime dt:
                date = DateOnly.FromDateTime(dt);
                break;
            default:
                return value.ToString() ?? string.Empty;
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var label = date == today
            ? "Today"
            : date == today.AddDays(-1)
                ? "Yesterday"
                : date.ToString("ddd, dd MMM yyyy", CultureInfo.CurrentCulture);

        return date == today || date == today.AddDays(-1)
            ? $"{label} · {date:ddd, dd MMM yyyy}"
            : label;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
