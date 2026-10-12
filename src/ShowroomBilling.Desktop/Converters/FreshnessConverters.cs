using System;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Contracts.Health;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// The tone of the chip beside a cached Tally list: fresh is green, aging is amber, stale is red, and a
/// list that has not been fetched, or a freshness this version does not know, is neutral.
/// </summary>
public sealed class FreshnessToneConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToLowerInvariant() switch
        {
            MasterFreshness.Fresh => "ok",
            MasterFreshness.Aging => "warn",
            MasterFreshness.Stale => "err",
            _ => "neutral",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Capitalises the first letter of a word, so a stored "fresh" reads as "Fresh" on a chip.</summary>
public sealed class SentenceCaseConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string { Length: > 0 } text
            ? char.ToUpper(text[0], culture) + text[1..]
            : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
