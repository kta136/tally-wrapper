using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

public sealed class InvoicePayloadMapperTests
{
    [Fact]
    public void BuildPayload_PreservesDiamondMapping()
    {
        var line = new BillLineViewModel
        {
            ItemMaster = TestMasters.Item("Diamond Ring", ItemUnits.Carat, category: ItemCategories.Diamond, stock: "Diamond Stock"),
            GrossWeight = 2m,
            DiamondRate = 3500m,
            EffectiveRate = 3500m,
            LineTotal = 7000m
        };

        var payload = InvoicePayloadMapper.BuildPayload(
            [line],
            new DateTimeOffset(2026, 4, 25, 10, 0, 0, TimeSpan.Zero),
            "Customer",
            "Notes",
            "Cash",
            rate24Kt: null,
            subtotal: 7000m,
            discount: 0m,
            cgst: 0m,
            sgst: 0m,
            roundOff: 0m,
            grandTotal: 7000m);

        var savedLine = payload.Lines.Single();
        Assert.Equal("diamond", savedLine.ItemCategory);
        Assert.Equal("wastage", savedLine.PricingMode);
        Assert.Equal("Diamond Stock", savedLine.StockName);
        Assert.Equal(2m, savedLine.Quantity);
    }
}
