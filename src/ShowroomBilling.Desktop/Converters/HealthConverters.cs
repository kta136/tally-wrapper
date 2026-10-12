using System;
using System.Globalization;
using System.Windows.Data;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Desktop.Converters;

/// <summary>
/// The word on the chip of a health card, from the dot's tone: green is "OK", amber "Check", red
/// "Problem", and a dot that has not been looked at, "Not checked".
/// </summary>
public sealed class DotToStatusWordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "ok" => "OK",
            "warn" => "Check",
            "err" => "Problem",
            _ => "Not checked",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"stock-items" as the row of the master table names it: "Stock items".</summary>
public sealed class MasterTypeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.Replace('_', '-') switch
        {
            MasterTypes.Companies => "Companies",
            MasterTypes.Ledgers => "Ledgers",
            MasterTypes.StockItems => "Stock items",
            MasterTypes.VoucherTypes => "Voucher types",
            var other => other ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The freshness of one cached list in words: "Fresh", "Aging", "Stale", "Not fetched".</summary>
public sealed class FreshnessLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToLowerInvariant() switch
        {
            MasterFreshness.Fresh => "Fresh",
            MasterFreshness.Aging => "Aging",
            MasterFreshness.Stale => "Stale",
            MasterFreshness.Missing => "Not fetched",
            null or "" => "Not fetched",
            var other => char.ToUpper(other[0], culture) + other[1..],
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>The chip beside "Master data": the overall freshness of the four cached lists.</summary>
public sealed class MastersOverallLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string)?.ToLowerInvariant() switch
        {
            MasterFreshness.Fresh => "All fresh",
            MasterFreshness.Aging => "Some aging",
            MasterFreshness.Stale => "Stale",
            MasterFreshness.Missing => "Not fetched",
            null or "" => "Not checked",
            var other => char.ToUpper(other[0], culture) + other[1..],
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
