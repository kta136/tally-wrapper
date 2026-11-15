using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels;

/// <summary>
/// The items and karats of the saved settings, as the Invoice tab, a hand-over
/// and the batch scheduler read them. It never changes: when settings are
/// loaded or saved a new one is published
/// (<see cref="Settings.SettingsViewModel.Catalog"/>), so nothing typed into
/// Settings and not yet saved reaches a bill.
/// </summary>
public sealed record MasterCatalog(IReadOnlyList<ItemMasterEntry> Items, IReadOnlyList<KaratMasterEntry> Karats)
{
    public static MasterCatalog Empty { get; } = new([], []);

    /// <summary>Reads the saved lists; a blank unit, category or pricing mode takes its default, as it always has.</summary>
    public static MasterCatalog From(MasterDataSettingsDto? masters) => new(
        MasterDataJson.ParseItemsOrEmpty(masters?.ItemMasterDataJson).Select(Normalise).ToList(),
        MasterDataJson.ParseKaratsOrEmpty(masters?.KaratMappingDataJson).Select(Normalise).ToList());

    /// <summary>The item of that name, ignoring case and the spaces around it.</summary>
    public ItemMasterEntry? FindItem(string? name)
    {
        var wanted = name?.Trim();
        return string.IsNullOrEmpty(wanted)
            ? null
            : Items.FirstOrDefault(item => string.Equals(item.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The karat with that label, ignoring case.</summary>
    public KaratMasterEntry? FindKarat(string? label)
        => string.IsNullOrWhiteSpace(label)
            ? null
            : Karats.FirstOrDefault(karat => string.Equals(karat.Label, label, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the two hold the same entries in the same order, so a load that changed nothing need not be published.</summary>
    public bool HasSameEntriesAs(MasterCatalog other)
        => Items.SequenceEqual(other.Items) && Karats.SequenceEqual(other.Karats);

    private static ItemMasterEntry Normalise(ItemMasterEntry entry) => entry with
    {
        Name = entry.Name ?? string.Empty,
        Unit = string.IsNullOrWhiteSpace(entry.Unit) ? ItemUnits.Gram : entry.Unit,
        ItemCategory = string.IsNullOrWhiteSpace(entry.ItemCategory) ? ItemCategories.GoldBased : entry.ItemCategory,
        PricingMode = string.IsNullOrWhiteSpace(entry.PricingMode) ? PricingModes.Wastage : entry.PricingMode,
        DefaultStockMappingLabel = entry.DefaultStockMappingLabel ?? string.Empty,
    };

    private static KaratMasterEntry Normalise(KaratMasterEntry entry) => entry with
    {
        Label = entry.Label ?? string.Empty,
        TallyItem = entry.TallyItem ?? string.Empty,
    };
}
