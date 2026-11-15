using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>Items and karats for tests, and a way to give a settings view model saved ones without a server.</summary>
internal static class TestMasters
{
    public static ItemMasterEntry Item(
        string name,
        string? unit = null,
        decimal wastage = 0m,
        string? category = null,
        string? pricing = null,
        decimal labour = 0m,
        string stock = "") => new(
            Name: name,
            Unit: unit ?? ItemUnits.Gram,
            WastagePercent: wastage,
            ItemCategory: category ?? ItemCategories.GoldBased,
            PricingMode: pricing ?? PricingModes.Wastage,
            DefaultLabourPerGram: labour,
            DefaultStockMappingLabel: stock);

    public static KaratMasterEntry Karat(string label, decimal purity = 91.6m, string tallyItem = "Gold Ornaments")
        => new(label, purity, tallyItem);

    /// <summary>
    /// Publishes these as the saved items and karats, as a load or a save of the settings does.
    /// A list left out stays as it is.
    /// </summary>
    public static void Publish(
        this SettingsViewModel settings,
        IEnumerable<ItemMasterEntry>? items = null,
        IEnumerable<KaratMasterEntry>? karats = null)
        => settings.Catalog = new MasterCatalog(
            (items ?? settings.Catalog.Items).ToList(),
            (karats ?? settings.Catalog.Karats).ToList());
}
