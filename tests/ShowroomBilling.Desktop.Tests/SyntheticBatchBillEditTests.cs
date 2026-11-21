using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// A batch bill opened for edit is priced as it was made. Its lines carry the pricing mode and
/// category the Invoice tab saves with a line; without them the mode came from the item master as
/// it was by then, and a line made with labour lost it once the item was changed or removed.
/// </summary>
public sealed class SyntheticBatchBillEditTests
{
    [Theory]
    [InlineData(PricingModes.Both)]
    [InlineData(PricingModes.Labour)]
    [InlineData(PricingModes.Wastage)]
    public void A_batch_line_keeps_its_pricing_mode_when_the_item_master_changes_or_goes(string madeWith)
    {
        var item = new ItemMasterEntry("Gold Bangle", ItemUnits.Gram, 5m, ItemCategories.GoldBased, madeWith, 200m, "");
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var request = new SyntheticBatchRequest(300_000, 199_000, 13_000m, "Cash", 1, 2, start, start.AddHours(5), ["22K"]);

        var lines = new SyntheticBillPlanner()
            .BuildPlan(request, [item], [new KaratMasterEntry("22K", 92m, "Gold 22K")], new Random(4), earliestUtc: null, nowUtc: start.AddDays(1))
            .Bills.SelectMany(bill => bill.Payload.Lines).ToList();

        var changedTo = madeWith == PricingModes.Wastage ? PricingModes.Both : PricingModes.Wastage;
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.Equal(madeWith, InvoicePayloadMapper.ResolveLinePricingMode(line, item with { PricingMode = changedTo }));
            Assert.Equal(madeWith, InvoicePayloadMapper.ResolveLinePricingMode(line, itemMaster: null));
            Assert.Equal(ItemCategories.GoldBased, InvoicePayloadMapper.ResolveLineItemCategory(line, itemMaster: null));
        });
    }
}
