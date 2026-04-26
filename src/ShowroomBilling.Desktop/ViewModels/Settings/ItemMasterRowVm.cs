using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class ItemMasterRowVm : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string unit = ItemUnits.Gram;
    [ObservableProperty] private string wastagePercent = "0";
    [ObservableProperty] private string itemCategory = ItemCategories.GoldBased;
    [ObservableProperty] private string pricingMode = PricingModes.Wastage;
    [ObservableProperty] private string defaultLabourPerGram = "0";
    [ObservableProperty] private string defaultStockMappingLabel = string.Empty;

    public static ItemMasterRowVm FromEntry(ItemMasterEntry e) => new()
    {
        Name = e.Name,
        Unit = string.IsNullOrWhiteSpace(e.Unit) ? ItemUnits.Gram : e.Unit,
        WastagePercent = e.WastagePercent.ToString(CultureInfo.InvariantCulture),
        ItemCategory = string.IsNullOrWhiteSpace(e.ItemCategory) ? ItemCategories.GoldBased : e.ItemCategory,
        PricingMode = string.IsNullOrWhiteSpace(e.PricingMode) ? PricingModes.Wastage : e.PricingMode,
        DefaultLabourPerGram = e.DefaultLabourPerGram.ToString(CultureInfo.InvariantCulture),
        DefaultStockMappingLabel = e.DefaultStockMappingLabel ?? string.Empty,
    };

    public bool TryBuildEntry(out ItemMasterEntry entry, out string error)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(Name)) { error = "Item master row: name is required."; return false; }
        if (!decimal.TryParse(WastagePercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var wastage) || wastage < 0)
        { error = $"Item '{Name}': wastage % must be a non-negative number."; return false; }
        if (!decimal.TryParse(DefaultLabourPerGram, NumberStyles.Number, CultureInfo.InvariantCulture, out var labour) || labour < 0)
        { error = $"Item '{Name}': labour/gram must be a non-negative number."; return false; }
        if (!ItemCategories.All.Contains(ItemCategory))
        { error = $"Item '{Name}': category must be one of {string.Join(", ", ItemCategories.All)}."; return false; }
        if (!PricingModes.All.Contains(PricingMode))
        { error = $"Item '{Name}': pricing mode must be one of {string.Join(", ", PricingModes.All)}."; return false; }

        entry = new ItemMasterEntry(
            Name: Name.Trim(),
            Unit: string.IsNullOrWhiteSpace(Unit) ? ItemUnits.Gram : Unit.Trim(),
            WastagePercent: wastage,
            ItemCategory: ItemCategory,
            PricingMode: PricingMode,
            DefaultLabourPerGram: labour,
            DefaultStockMappingLabel: (DefaultStockMappingLabel ?? string.Empty).Trim());
        error = string.Empty;
        return true;
    }
}
