using System.Text.Json.Serialization;

namespace ShowroomBilling.Contracts.Settings;

public static class ItemCategories
{
    public const string GoldBased = "gold_based";
    public const string Diamond = "diamond";

    public static IReadOnlyList<string> All { get; } = new[] { GoldBased, Diamond };
}

public static class PricingModes
{
    public const string Wastage = "wastage";
    public const string Labour = "labour";
    public const string Both = "both";

    public static IReadOnlyList<string> All { get; } = new[] { Wastage, Labour, Both };
}

public static class ItemUnits
{
    public const string Gram = "gm";
    public const string Carat = "ct";

    public static IReadOnlyList<string> All { get; } = new[] { Gram, Carat };
}

public sealed record ItemMasterEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("wastage_percent")] decimal WastagePercent,
    [property: JsonPropertyName("item_category")] string ItemCategory,
    [property: JsonPropertyName("pricing_mode")] string PricingMode,
    [property: JsonPropertyName("default_labour_per_gram")] decimal DefaultLabourPerGram,
    [property: JsonPropertyName("default_stock_mapping_label")] string DefaultStockMappingLabel);
