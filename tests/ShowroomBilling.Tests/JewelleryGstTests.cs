using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Tests;

public sealed class JewelleryGstTests
{
    [Theory]
    [InlineData("3000.00", "1500.00", "1500.00")]
    [InlineData("2999.99", "1500.00", "1499.99")]
    [InlineData("0.01", "0.01", "0.00")]
    [InlineData("0.00", "0.00", "0.00")]
    public void Split_gives_the_odd_paisa_to_cgst_and_always_adds_back_up(string tax, string cgst, string sgst)
    {
        var taxTotal = decimal.Parse(tax, System.Globalization.CultureInfo.InvariantCulture);

        var split = JewelleryGst.Split(taxTotal);

        Assert.Equal(decimal.Parse(cgst, System.Globalization.CultureInfo.InvariantCulture), split.Cgst);
        Assert.Equal(decimal.Parse(sgst, System.Globalization.CultureInfo.InvariantCulture), split.Sgst);
        Assert.Equal(taxTotal, split.Cgst + split.Sgst);
    }
}
