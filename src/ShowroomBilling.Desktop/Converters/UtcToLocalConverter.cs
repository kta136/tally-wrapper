using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// Shifts a UTC timestamp from the API to the workstation's local time for
/// display. A <see cref="DateTimeOffset"/> formats in its own offset, so
/// binding one straight to <c>StringFormat</c> would show UTC. The binding's
/// <c>StringFormat</c> and <c>TargetNullValue</c> still apply to the result.
/// </summary>
public sealed class UtcToLocalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            DateTimeOffset offset => offset.ToLocalTime(),
            DateTime { Kind: DateTimeKind.Local } local => local,
            DateTime utc => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime(),
            _ => value,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
