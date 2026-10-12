using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class ItemMasterRowVm : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerSummary), nameof(QuickSummary))]
    private string unit = ItemUnits.Gram;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerSummary), nameof(QuickSummary))]
    private string wastagePercent = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerSummary), nameof(QuickSummary))]
    private string itemCategory = ItemCategories.GoldBased;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerSummary), nameof(QuickSummary))]
    private string pricingMode = PricingModes.Wastage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PickerSummary), nameof(QuickSummary))]
    private string defaultLabourPerGram = "0";
    [ObservableProperty] private string defaultStockMappingLabel = string.Empty;

    /// <summary>What the item list under a line's item cell shows beneath the name: "gm · 8% wastage · ₹320/g labour".</summary>
    public string PickerSummary => Summarise(withUnit: true, labourWord: " labour");

    /// <summary>The shorter line of the Quick add list: "8% wastage · ₹320/g".</summary>
    public string QuickSummary => Summarise(withUnit: false, labourWord: string.Empty);

    private string Summarise(bool withUnit, string labourWord)
    {
        var parts = new List<string>();
        if (withUnit && !string.IsNullOrWhiteSpace(Unit))
        {
            parts.Add(Unit);
        }

        if (string.Equals(ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"flat rate per {(string.IsNullOrWhiteSpace(Unit) ? ItemUnits.Carat : Unit)}");
            return string.Join(" · ", parts);
        }

        var mode = string.IsNullOrWhiteSpace(PricingMode) ? PricingModes.Wastage : PricingMode;
        if (mode != PricingModes.Labour)
        {
            parts.Add($"{WastagePercent}% wastage");
        }

        if (mode == PricingModes.Labour || mode == PricingModes.Both)
        {
            parts.Add($"₹{DefaultLabourPerGram}/g{labourWord}");
        }

        return string.Join(" · ", parts);
    }

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
