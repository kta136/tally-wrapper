using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Tests;

/// <summary>
/// Regression guard for the print pipeline's Tally-name substitution. The bill display
/// must show the Tally stock-item name, not the program-side <c>ItemName</c>. Primary
/// source is <see cref="BillLineItemDto.StockName"/> (set at save time by
/// <c>InvoicePayloadMapper.ResolveStockName</c>); the karat-master snapshot on
/// <c>CompanyProfileProvider</c> is only a fallback for older bills that lack
/// <c>StockName</c>. Previous regression: substitution required the karat snapshot to be
/// loaded, so prints fell back to <c>ItemName</c> whenever the snapshot was empty.
/// </summary>
public class PrintLineSubstitutionTests
{
    [Fact]
    public void StockName_on_line_overrides_ItemName_even_without_karat_mappings()
    {
        var line = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: "22 ct gold ornaments");

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, karatMappings: null);

        Assert.Equal("22 ct gold ornaments", result[0].ItemName);
    }

    [Fact]
    public void StockName_on_line_overrides_ItemName_with_empty_karat_mappings()
    {
        var line = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: "22 ct gold ornaments");

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, Array.Empty<KaratMasterEntry>());

        Assert.Equal("22 ct gold ornaments", result[0].ItemName);
    }

    [Fact]
    public void StockName_is_trimmed_before_substitution()
    {
        var line = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: "  22 ct gold ornaments  ");

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, karatMappings: null);

        Assert.Equal("22 ct gold ornaments", result[0].ItemName);
    }

    [Fact]
    public void Falls_back_to_karat_master_when_StockName_missing()
    {
        var line = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: null);
        var mappings = new[] { new KaratMasterEntry("22K", 91.6m, "22 ct gold ornaments") };

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, mappings);

        Assert.Equal("22 ct gold ornaments", result[0].ItemName);
    }

    [Fact]
    public void Keeps_ItemName_when_no_StockName_and_no_karat_match()
    {
        var line = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: null);
        var mappings = new[] { new KaratMasterEntry("18K", 75m, "18 ct gold ornaments") };

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, mappings);

        Assert.Equal("Necklace", result[0].ItemName);
    }

    [Fact]
    public void Blank_StockName_falls_through_to_karat_lookup()
    {
        var line = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: "   ");
        var mappings = new[] { new KaratMasterEntry("22K", 91.6m, "22 ct gold ornaments") };

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, mappings);

        Assert.Equal("22 ct gold ornaments", result[0].ItemName);
    }

    [Fact]
    public void Diamond_line_uses_StockName_when_set()
    {
        var line = DiamondLine(itemName: "Diamond Ring", stockName: "Loose diamonds");

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, karatMappings: null);

        Assert.Equal("Loose diamonds", result[0].ItemName);
    }

    [Fact]
    public void Diamond_line_keeps_ItemName_when_StockName_missing()
    {
        // MapTallyItemName skips diamonds, so without StockName there's no source and
        // the original ItemName is preserved.
        var line = DiamondLine(itemName: "Diamond Ring", stockName: null);
        var mappings = new[] { new KaratMasterEntry("22K", 91.6m, "22 ct gold ornaments") };

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { line }, mappings);

        Assert.Equal("Diamond Ring", result[0].ItemName);
    }

    [Fact]
    public void Mixed_lines_substitute_independently()
    {
        var saved = NonDiamondLine(itemName: "Necklace", karat: "22K", stockName: "22 ct gold ornaments");
        var legacy = NonDiamondLine(itemName: "Bangle", karat: "18K", stockName: null);
        var unmapped = NonDiamondLine(itemName: "Pendant", karat: "14K", stockName: null);
        var mappings = new[] { new KaratMasterEntry("18K", 75m, "18 ct gold ornaments") };

        var result = PrintLineSubstitution.WithTallyMappedItemNames(new[] { saved, legacy, unmapped }, mappings);

        Assert.Equal("22 ct gold ornaments", result[0].ItemName);
        Assert.Equal("18 ct gold ornaments", result[1].ItemName);
        Assert.Equal("Pendant", result[2].ItemName);
    }

    [Fact]
    public void Empty_lines_returns_input_unchanged()
    {
        var lines = Array.Empty<BillLineItemDto>();

        var result = PrintLineSubstitution.WithTallyMappedItemNames(lines, karatMappings: null);

        Assert.Same(lines, result);
    }

    private static BillLineItemDto NonDiamondLine(string itemName, string karat, string? stockName) => new(
        ItemName: itemName,
        HsnCode: "711319",
        Quantity: 10m,
        Unit: "g",
        Rate: 6000m,
        LineTotal: 60000m,
        Karat: karat,
        RawJson: null,
        StockName: stockName,
        ItemCategory: ItemCategories.GoldBased);

    private static BillLineItemDto DiamondLine(string itemName, string? stockName) => new(
        ItemName: itemName,
        HsnCode: "711319",
        Quantity: 1m,
        Unit: "ct",
        Rate: 45000m,
        LineTotal: 45000m,
        Karat: null,
        RawJson: null,
        StockName: stockName,
        DiamondRate: 45000m,
        ItemCategory: ItemCategories.Diamond);
}
