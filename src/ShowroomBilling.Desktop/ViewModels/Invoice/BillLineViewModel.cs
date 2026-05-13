using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

public partial class BillLineViewModel : ObservableObject
{
    private int rowNumber;

    [ObservableProperty] private string itemName = string.Empty;
    [ObservableProperty] private decimal? grossWeight;
    [ObservableProperty] private decimal? lessWeight;
    [ObservableProperty] private string unit = ItemUnits.Gram;
    [ObservableProperty] private string karat = string.Empty;
    [ObservableProperty] private decimal? wastagePercent;
    [ObservableProperty] private decimal? labourPerUnit;
    [ObservableProperty] private decimal? extra;
    [ObservableProperty] private decimal? diamondRate;
    [ObservableProperty] private string itemCategory = ItemCategories.GoldBased;
    [ObservableProperty] private string pricingMode = PricingModes.Wastage;
    [ObservableProperty] private string? stockName;

    [ObservableProperty] private ItemMasterRowVm? itemMaster;
    [ObservableProperty] private KaratMasterRowVm? karatMaster;

    [ObservableProperty] private decimal effectiveRate;
    [ObservableProperty] private decimal lineTotal;

    public int RowNumber
    {
        get => rowNumber;
        set => SetProperty(ref rowNumber, value);
    }

    public decimal NetWeight => IsDiamond
        ? GrossWeight ?? 0m
        : Math.Max(0m, (GrossWeight ?? 0m) - (LessWeight ?? 0m));

    public bool IsEmpty => string.IsNullOrWhiteSpace(ItemName);
    public string ResolvedItemCategory => !string.IsNullOrWhiteSpace(ItemCategory)
        ? ItemCategory
        : ItemMaster?.ItemCategory ?? ItemCategories.GoldBased;
    public bool IsDiamond => string.Equals(ResolvedItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase);
    public bool IsGoldLine => !IsDiamond;
    public string ResolvedPricingMode => !string.IsNullOrWhiteSpace(PricingMode)
        ? PricingMode
        : ItemMaster?.PricingMode ?? PricingModes.Wastage;

    public event EventHandler? MutationOccurred;

    partial void OnItemNameChanged(string value)
    {
        if (ItemMaster is null)
        {
            var shouldBeDiamond = !string.IsNullOrEmpty(value)
                && value.Contains("diamond", StringComparison.OrdinalIgnoreCase);
            ItemCategory = shouldBeDiamond ? ItemCategories.Diamond : ItemCategories.GoldBased;
        }
        Raise();
    }
    partial void OnGrossWeightChanged(decimal? value)
    {
        OnPropertyChanged(nameof(NetWeight));
        Raise();
    }
    partial void OnLessWeightChanged(decimal? value)
    {
        if (IsDiamond && (value ?? 0m) != 0m)
        {
            LessWeight = 0m;
            return;
        }
        OnPropertyChanged(nameof(NetWeight));
        Raise();
    }
    partial void OnUnitChanged(string value) => Raise();
    partial void OnKaratChanged(string value) => Raise();
    partial void OnWastagePercentChanged(decimal? value) => Raise();
    partial void OnLabourPerUnitChanged(decimal? value) => Raise();
    partial void OnExtraChanged(decimal? value) => Raise();
    partial void OnDiamondRateChanged(decimal? value) => Raise();
    partial void OnStockNameChanged(string? value) => Raise();
    partial void OnItemCategoryChanged(string value)
    {
        NormalizeDiamondFields();
        OnPropertyChanged(nameof(ResolvedItemCategory));
        OnPropertyChanged(nameof(IsDiamond));
        OnPropertyChanged(nameof(IsGoldLine));
        OnPropertyChanged(nameof(NetWeight));
        Raise();
    }
    partial void OnPricingModeChanged(string value)
    {
        OnPropertyChanged(nameof(ResolvedPricingMode));
        Raise();
    }

    partial void OnItemMasterChanged(ItemMasterRowVm? value)
    {
        if (value is not null)
        {
            ItemName = value.Name;
            if (!string.IsNullOrWhiteSpace(value.Unit)) Unit = value.Unit;
            ItemCategory = string.IsNullOrWhiteSpace(value.ItemCategory) ? ItemCategories.GoldBased : value.ItemCategory;
            PricingMode = string.IsNullOrWhiteSpace(value.PricingMode) ? PricingModes.Wastage : value.PricingMode;
            if (!IsDiamond)
            {
                if (decimal.TryParse(value.WastagePercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var w))
                    WastagePercent = w;
                if (decimal.TryParse(value.DefaultLabourPerGram, NumberStyles.Number, CultureInfo.InvariantCulture, out var l))
                    LabourPerUnit = l;
            }
        }
        NormalizeDiamondFields();
        OnPropertyChanged(nameof(ResolvedItemCategory));
        OnPropertyChanged(nameof(IsDiamond));
        OnPropertyChanged(nameof(IsGoldLine));
        OnPropertyChanged(nameof(NetWeight));
        OnPropertyChanged(nameof(ResolvedPricingMode));
        Raise();
    }

    partial void OnKaratMasterChanged(KaratMasterRowVm? value)
    {
        if (IsDiamond)
        {
            if (value is not null) KaratMaster = null;
            if (!string.IsNullOrWhiteSpace(Karat)) Karat = string.Empty;
            Raise();
            return;
        }

        if (value is not null)
        {
            Karat = value.Label;
        }
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsEmpty));
        MutationOccurred?.Invoke(this, EventArgs.Empty);
    }

    private void NormalizeDiamondFields()
    {
        if (!IsDiamond) return;

        if (LessWeight != 0m) LessWeight = 0m;
        if (!string.IsNullOrWhiteSpace(Karat)) Karat = string.Empty;
        if (KaratMaster is not null) KaratMaster = null;
        if (WastagePercent != 0m) WastagePercent = 0m;
        if (LabourPerUnit != 0m) LabourPerUnit = 0m;
    }
}
