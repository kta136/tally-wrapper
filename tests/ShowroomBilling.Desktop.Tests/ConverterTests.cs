using System.Globalization;
using ShowroomBilling.Desktop.Converters;

namespace ShowroomBilling.Desktop.Tests;

public sealed class ConverterTests
{
    [Fact]
    public void The_bills_date_header_names_today_and_yesterday()
    {
        var converter = new BillDateGroupHeaderConverter();
        var today = DateOnly.FromDateTime(DateTime.Today);
        string Header(object? value) => (string)converter.Convert(value, typeof(string), null, CultureInfo.CurrentCulture);

        Assert.Equal($"Today · {today:ddd, dd MMM yyyy}", Header(today));
        Assert.Equal($"Yesterday · {today.AddDays(-1):ddd, dd MMM yyyy}", Header(today.AddDays(-1)));
        Assert.Equal($"{today.AddDays(-9):ddd, dd MMM yyyy}", Header(today.AddDays(-9)));
        Assert.Equal($"Today · {today:ddd, dd MMM yyyy}", Header(DateTime.Today));
        Assert.Equal("No date", Header(null));
    }

    [Theory]
    [InlineData("0.001", true)]
    [InlineData("0", false)]
    [InlineData("-2", false)]
    [InlineData(null, false)]
    public void A_cell_is_tinted_only_for_an_amount_above_zero(string? amount, bool expected)
    {
        decimal? value = amount is null ? null : decimal.Parse(amount, CultureInfo.InvariantCulture);

        Assert.Equal(expected, new DecimalIsPositiveConverter().Convert(value, typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
