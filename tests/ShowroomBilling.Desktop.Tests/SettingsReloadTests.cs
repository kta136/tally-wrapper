using System.Text.Json;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// What a load of the settings may and may not disturb: an edit in progress,
/// and the item and karat rows that the lines of an open bill point at.
/// </summary>
public sealed class SettingsReloadTests
{
    [Fact]
    public async Task Coming_back_to_the_settings_tab_mid_edit_keeps_what_was_typed()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m))));
        var settings = new SettingsViewModel(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        settings.BeginEditCommand.Execute(null);
        settings.Draft.PrintCompanyName = "Typed, not saved";
        settings.Draft.ItemMasterRows.Single().WastagePercent = "9";
        Assert.True(settings.IsDirty);

        // The operator looks at another tab and comes back.
        await settings.RefreshOnShownAsync();

        Assert.Equal(1, api.GetCount);
        Assert.True(settings.IsEditing);
        Assert.True(settings.IsDirty);
        Assert.Equal("Typed, not saved", settings.Draft.PrintCompanyName);
        Assert.Equal("9", settings.Draft.ItemMasterRows.Single().WastagePercent);
    }

    [Fact]
    public async Task Coming_back_to_the_settings_tab_with_no_edit_open_loads_what_the_server_has_now()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m))));
        var settings = new SettingsViewModel(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);

        api.Response = Response(Items(Item("Gold Ring", wastage: 10m)));
        await settings.RefreshOnShownAsync();

        Assert.Equal(2, api.GetCount);
        Assert.Equal("10", settings.Draft.ItemMasterRows.Single().WastagePercent);
    }

    [Fact]
    public async Task A_reload_keeps_the_item_and_karat_an_open_bills_line_points_at()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m)), Karats("22K")));
        var settings = new SettingsViewModel(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var invoice = new InvoiceViewModel(null, null, settings);
        var line = invoice.Lines[0];
        var item = settings.Draft.ItemMasterRows.Single();
        var karat = settings.Draft.KaratRows.Single();
        line.ItemMaster = item;
        line.KaratMaster = karat;
        line.WastagePercent = 5m;   // typed over the item's 8 %

        // A visit to the Settings tab, then Edit and Discard: each loads the draft again.
        await settings.RefreshOnShownAsync();
        settings.BeginEditCommand.Execute(null);
        settings.DiscardChangesCommand.Execute(null);

        Assert.Same(item, settings.Draft.ItemMasterRows.Single());
        Assert.Same(karat, settings.Draft.KaratRows.Single());
        Assert.Same(item, line.ItemMaster);
        Assert.Same(karat, line.KaratMaster);

        // The next change to the line must not bring the item's default back.
        line.GrossWeight = 10m;
        Assert.Equal(5m, line.WastagePercent);
    }

    [Fact]
    public void A_load_updates_rows_in_place_and_adds_and_removes_the_rest_in_the_loaded_order()
    {
        var draft = new SettingsDraft();
        draft.LoadFrom(Response(Items(Item("Gold Ring", 8m), Item("Gold Chain", 10m))).Settings);
        var ring = draft.ItemMasterRows[0];
        var chain = draft.ItemMasterRows[1];

        // The ring is gone, a coin comes first, and the chain's wastage changed.
        draft.LoadFrom(Response(Items(Item("Gold Coin", 2m), Item("gold chain ", 12m))).Settings);

        Assert.Equal(["Gold Coin", "gold chain "], draft.ItemMasterRows.Select(row => row.Name));
        Assert.Same(chain, draft.ItemMasterRows[1]);
        Assert.Equal("12", chain.WastagePercent);
        Assert.DoesNotContain(ring, draft.ItemMasterRows);
    }

    [Fact]
    public void A_row_kept_across_loads_reports_a_change_once()
    {
        var draft = new SettingsDraft();
        var loaded = Response(Items(Item("Gold Ring", 8m))).Settings;
        draft.LoadFrom(loaded);
        draft.LoadFrom(loaded);
        draft.LoadFrom(loaded);
        var changes = 0;
        draft.PropertyChanged += (_, _) => changes++;

        // A field that raises one notification of its own (wastage raises three: itself and two summaries).
        draft.ItemMasterRows.Single().DefaultStockMappingLabel = "Gold Ornaments";

        Assert.Equal(1, changes);
    }

    private static string Items(params ItemMasterEntry[] items) => JsonSerializer.Serialize(items);

    private static string Karats(params string[] labels) =>
        JsonSerializer.Serialize(labels.Select(label => new KaratMasterEntry(label, 91.6m, "Gold Ornaments")));

    private static ItemMasterEntry Item(string name, decimal wastage) => new(
        Name: name,
        Unit: ItemUnits.Gram,
        WastagePercent: wastage,
        ItemCategory: ItemCategories.GoldBased,
        PricingMode: PricingModes.Wastage,
        DefaultLabourPerGram: 0m,
        DefaultStockMappingLabel: string.Empty);

    private static EffectiveSettingsResponse Response(string itemsJson, string karatsJson = "[]") =>
        new(
            "cloud",
            "settings",
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Development Company"),
                new NumberingSettingsDto("DEV-", string.Empty, 4),
                new PrintSettingsDto("Shop", null, null, null, null, "India", null, null, null, null, null, true, false, false, 11, 9),
                new LedgerMappingsDto("Sales", "Cash", "Card / UPI", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                new MasterDataSettingsDto(itemsJson, karatsJson)),
            [],
            [],
            DateTimeOffset.UtcNow);

    private sealed class FakeSettingsApi(EffectiveSettingsResponse response) : ISettingsApiClient
    {
        public EffectiveSettingsResponse Response { get; set; } = response;
        public int GetCount { get; private set; }

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(Response);
        }

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            SelectActiveCompanyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
