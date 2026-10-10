using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Tests;

public sealed class InvoiceNumberFormatterTests
{
    [Theory]
    [InlineData("SR/", null, 42, "2026-27", 4, "SR/2026-27/0042")]
    [InlineData(null, null, 42, "2026-27", 4, "2026-27/0042")]
    [InlineData("SR/", "-A", 42, "2026-27", 6, "SR/2026-27/000042-A")]
    [InlineData("SR/", null, 12345, "2026-27", 4, "SR/2026-27/12345")]
    [InlineData("   ", "  ", 7, "2026-27", 4, "2026-27/0007")]
    public void Format_puts_the_fiscal_year_between_prefix_and_padded_core(
        string? prefix, string? suffix, long value, string fiscalYear, int padding, string expected)
    {
        Assert.Equal(expected, InvoiceNumberFormatter.Format(prefix, suffix, value, fiscalYear, padding));
    }

    [Theory]
    [InlineData("SR/2026-27/", "SR/2026-27/0042")]
    [InlineData("SR/26-27/", "SR/26-27/0042")]
    public void Format_does_not_repeat_a_fiscal_year_the_prefix_already_carries(string prefix, string expected)
    {
        Assert.Equal(expected, InvoiceNumberFormatter.Format(prefix, null, 42, "2026-27"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Format_falls_back_to_the_default_padding(int padding)
    {
        Assert.Equal("SR/2026-27/0042", InvoiceNumberFormatter.Format("SR/", null, 42, "2026-27", padding));
    }

    [Theory]
    [InlineData("DDAJR/26-27/0049", 49L)]
    [InlineData("DDAJR/26-27/49", 49L)]
    [InlineData("INV-A2", 2L)]
    [InlineData("LEGACY-X", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TryParseTrailingCore_reads_only_the_trailing_digits(string? invoiceNumber, long? expected)
    {
        Assert.Equal(expected, InvoiceNumberFormatter.TryParseTrailingCore(invoiceNumber));
    }

    [Theory]
    [InlineData(2026, 3, 31, "2025-26")]
    [InlineData(2026, 4, 1, "2026-27")]
    [InlineData(2099, 12, 31, "2099-00")]
    public void Fiscal_year_turns_over_on_the_first_of_april(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, InvoiceNumberFormatter.ComputeFiscalYear(new DateOnly(year, month, day)));
    }

    [Fact]
    public void Fiscal_year_of_an_instant_is_read_on_the_indian_calendar()
    {
        // 18:30 UTC on 31 March is midnight on 1 April in India.
        var lastSecondOfOldYear = new DateTimeOffset(2026, 3, 31, 18, 29, 59, TimeSpan.Zero);
        var firstSecondOfNewYear = new DateTimeOffset(2026, 3, 31, 18, 30, 0, TimeSpan.Zero);

        Assert.Equal("2025-26", InvoiceNumberFormatter.ComputeFiscalYear(lastSecondOfOldYear));
        Assert.Equal("2026-27", InvoiceNumberFormatter.ComputeFiscalYear(firstSecondOfNewYear));
    }
}
