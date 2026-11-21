using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// A unit that is neither "gm" nor "ct" is still offered by its own row's Unit drop-down, so the
/// drop-down shows it. Item lists saved by older builds write grams as "g"; such an item, and every
/// bill line made from it, had an empty Unit cell. The unit is never rewritten: it is printed and
/// sent to Tally as it is saved.
/// </summary>
public sealed class UnitChoicesTests
{
    [Fact]
    public void A_bill_line_offers_its_own_unit_beside_grams_and_carats()
    {
        var line = new BillLineViewModel();
        Assert.Equal(["gm", "ct"], line.UnitChoices);

        line.Unit = "g";
        Assert.Equal(["gm", "ct", "g"], line.UnitChoices);

        // Going back to a unit already offered adds nothing, and a unit once offered stays, so
        // the list under an open drop-down only ever grows.
        line.Unit = "gm";
        line.Unit = "g";
        Assert.Equal(["gm", "ct", "g"], line.UnitChoices);
    }

    [Fact]
    public void A_saved_bill_line_opened_for_edit_keeps_and_offers_the_unit_it_was_saved_with()
    {
        var line = new BillLineViewModel();

        line.ApplyPayloadValues(
            new BillLineItemDto("Gold Ring", "7113", 10m, "g", 100m, 1_000m, "22K", null),
            ItemCategories.GoldBased,
            PricingModes.Wastage,
            itemMaster: null,
            karatMaster: null);

        Assert.Equal("g", line.Unit);
        Assert.Contains("g", line.UnitChoices);
    }

    [Fact]
    public void An_item_saved_with_grams_as_g_shows_its_unit_in_Settings_and_is_saved_with_it()
    {
        var row = ItemMasterRowVm.FromEntry(new ItemMasterEntry("Gold Ring", "g", 8m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, ""));

        Assert.Equal("g", row.Unit);
        Assert.Equal(["gm", "ct", "g"], row.UnitChoices);
        Assert.True(row.TryBuildEntry(out var saved, out _));
        Assert.Equal("g", saved.Unit);

        // A new item starts at grams as the app writes them, with nothing else offered.
        Assert.Equal(["gm", "ct"], new ItemMasterRowVm().UnitChoices);
    }
}
