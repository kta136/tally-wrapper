using System.Globalization;
using ShowroomBilling.Desktop.Converters;

namespace ShowroomBilling.Desktop.Tests;

public sealed class UtcToLocalConverterTests
{
    private static readonly UtcToLocalConverter Converter = new();

    [Fact]
    public void DateTimeOffset_keeps_the_instant_and_takes_the_local_offset()
    {
        var utc = new DateTimeOffset(2026, 10, 10, 8, 38, 0, TimeSpan.Zero);

        var result = Assert.IsType<DateTimeOffset>(Convert(utc));

        Assert.Equal(utc.UtcDateTime, result.UtcDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc), result.Offset);
    }

    [Fact]
    public void Unspecified_DateTime_is_treated_as_utc()
    {
        var utc = new DateTime(2026, 10, 10, 8, 38, 0, DateTimeKind.Unspecified);

        var result = Assert.IsType<DateTime>(Convert(utc));

        Assert.Equal(DateTimeKind.Local, result.Kind);
        Assert.Equal(DateTime.SpecifyKind(utc, DateTimeKind.Utc), result.ToUniversalTime());
    }

    [Fact]
    public void Null_stays_null_so_the_binding_can_show_its_placeholder()
    {
        Assert.Null(Convert(null));
    }

    private static object? Convert(object? value)
        => Converter.Convert(value, typeof(string), null, CultureInfo.InvariantCulture);
}
