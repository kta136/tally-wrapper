using System.Text.Json;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// What a load of the settings may and may not disturb: an edit in progress,
/// and what was typed on the lines of an open bill, which read the saved
/// items and karats and never the rows being edited.
/// </summary>
public sealed class SettingsReloadTests
{
    [Fact]
    public async Task Coming_back_to_the_settings_tab_mid_edit_keeps_what_was_typed()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m))));
        var settings = OfflineApi.Settings(api);
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
        var settings = OfflineApi.Settings(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);

        api.Response = Response(Items(Item("Gold Ring", wastage: 10m)));
        await settings.RefreshOnShownAsync();

        Assert.Equal(2, api.GetCount);
        Assert.Equal("10", settings.Draft.ItemMasterRows.Single().WastagePercent);
    }

    [Fact]
    public async Task A_load_that_brings_the_same_items_and_karats_leaves_an_open_bill_alone()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m)), Karats("22K")));
        var settings = OfflineApi.Settings(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings);
        var catalog = settings.Catalog;
        var line = invoice.Lines[0];
        var item = catalog.Items.Single();
        var karat = catalog.Karats.Single();
        line.ItemMaster = item;
        line.KaratMaster = karat;
        line.WastagePercent = 5m;   // typed over the item's 8 %

        // A visit to the Settings tab, then Edit and Discard: each loads the draft again.
        await settings.RefreshOnShownAsync();
        settings.BeginEditCommand.Execute(null);
        settings.DiscardChangesCommand.Execute(null);

        Assert.Equal(2, api.GetCount);
        Assert.Same(catalog, settings.Catalog);
        Assert.Same(item, line.ItemMaster);
        Assert.Same(karat, line.KaratMaster);

        // The next change to the line must not bring the item's default back.
        line.GrossWeight = 10m;
        Assert.Equal(5m, line.WastagePercent);
    }

    [Fact]
    public async Task A_load_that_brings_other_defaults_keeps_what_was_typed_on_an_open_bill()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m)), Karats(91.6m, "22K")));
        var settings = OfflineApi.Settings(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings)
        {
            Rate24Kt = 10_000m,
        };
        var line = invoice.Lines[0];
        line.ItemMaster = settings.Catalog.Items.Single();
        line.KaratMaster = settings.Catalog.Karats.Single();
        line.GrossWeight = 10m;
        line.WastagePercent = 5m;   // typed over the item's 8 %
        var totalBefore = line.LineTotal;

        // Another counter saved a new default wastage and a new purity; this one looks at Settings.
        api.Response = Response(Items(Item("Gold Ring", wastage: 10m)), Karats(92m, "22K"));
        await settings.RefreshOnShownAsync();

        // The line is on the items and karats as they are now, …
        Assert.Same(settings.Catalog.Items.Single(), line.ItemMaster);
        Assert.Same(settings.Catalog.Karats.Single(), line.KaratMaster);
        Assert.Equal(10m, line.ItemMaster!.WastagePercent);
        // … with its name and what was typed on it, priced at the karat's purity as it is now.
        Assert.Equal("Gold Ring", line.ItemName);
        Assert.Equal("22K", line.Karat);
        Assert.Equal(5m, line.WastagePercent);
        Assert.True(line.LineTotal > totalBefore);

        line.GrossWeight = 11m;
        Assert.Equal(5m, line.WastagePercent);
    }

    [Fact]
    public async Task An_item_that_is_no_longer_saved_leaves_the_line_with_its_name_and_values()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m), Item("Gold Chain", wastage: 6m))));
        var settings = OfflineApi.Settings(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings);
        var line = invoice.Lines[0];
        line.ItemMaster = settings.Catalog.Items[0];
        line.WastagePercent = 5m;

        api.Response = Response(Items(Item("Gold Chain", wastage: 6m)));
        await settings.RefreshOnShownAsync();

        Assert.Null(line.ItemMaster);
        Assert.Equal("Gold Ring", line.ItemName);
        Assert.Equal(5m, line.WastagePercent);
    }

    [Fact]
    public async Task What_is_typed_into_settings_reaches_a_bill_only_once_it_is_saved()
    {
        var api = new FakeSettingsApi(Response(Items(Item("Gold Ring", wastage: 8m))));
        var settings = OfflineApi.Settings(api);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings);

        settings.BeginEditCommand.Execute(null);
        settings.Draft.ItemMasterRows.Single().WastagePercent = "9";
        settings.Draft.ItemMasterRows.Add(new ItemMasterRowVm { Name = "Half typed" });

        // Not saved: the Invoice tab still has the one item, at 8 %.
        Assert.Equal(["Gold Ring"], invoice.ItemMasters.Select(item => item.Name));
        invoice.Lines[0].ItemName = "Gold Ring";
        Assert.Equal(8m, invoice.Lines[0].WastagePercent);

        settings.Draft.ItemMasterRows.RemoveAt(1);
        await settings.SaveAllCommand.ExecuteAsync(null);

        Assert.Equal(1, api.SaveCount);
        Assert.Equal(9m, invoice.ItemMasters.Single().WastagePercent);
        // The open line keeps its 8 %; a line started now takes the saved 9 %.
        Assert.Equal(8m, invoice.Lines[0].WastagePercent);
        invoice.Lines[^1].ItemName = "Gold Ring";
        Assert.Equal(9m, invoice.Lines[^2].WastagePercent);
    }

    [Fact]
    public void A_load_replaces_the_rows_with_the_loaded_ones_in_their_order()
    {
        var draft = new SettingsDraft();
        draft.LoadFrom(Response(Items(Item("Gold Ring", 8m), Item("Gold Chain", 10m))).Settings);

        // The ring is gone, a coin comes first, and the chain's wastage changed.
        draft.LoadFrom(Response(Items(Item("Gold Coin", 2m), Item("Gold Chain", 12m))).Settings);

        Assert.Equal(["Gold Coin", "Gold Chain"], draft.ItemMasterRows.Select(row => row.Name));
        Assert.Equal(["2", "12"], draft.ItemMasterRows.Select(row => row.WastagePercent));
    }

    [Fact]
    public void A_row_loaded_several_times_over_reports_a_change_once()
    {
        var draft = new SettingsDraft();
        var loaded = Response(Items(Item("Gold Ring", 8m))).Settings;
        draft.LoadFrom(loaded);
        draft.LoadFrom(loaded);
        draft.LoadFrom(loaded);
        var changes = 0;
        draft.PropertyChanged += (_, _) => changes++;

        draft.ItemMasterRows.Single().DefaultStockMappingLabel = "Gold Ornaments";

        Assert.Equal(1, changes);
    }

    [Fact]
    public void The_bank_charges_ledger_is_optional_and_always_sent()
    {
        var draft = new SettingsDraft();
        // A server older than the field sends none.
        var loaded = Response(Items()).Settings;
        draft.LoadFrom(loaded);
        Assert.Equal(string.Empty, draft.BankChargesLedger);

        Assert.True(draft.TryBuildDto(loaded, out var withNone, out var error), error);
        Assert.Equal(string.Empty, withNone.Ledgers.BankChargesLedger);

        draft.BankChargesLedger = "  Bank Charges Collected ";
        Assert.True(draft.TryBuildDto(loaded, out var withOne, out _));
        Assert.Equal("Bank Charges Collected", withOne.Ledgers.BankChargesLedger);

        draft.LoadFrom(withOne);
        Assert.Equal("Bank Charges Collected", draft.BankChargesLedger);
    }

    private static string Items(params ItemMasterEntry[] items) => JsonSerializer.Serialize(items);

    private static string Karats(params string[] labels) => Karats(91.6m, labels);

    private static string Karats(decimal purity, params string[] labels) =>
        JsonSerializer.Serialize(labels.Select(label => new KaratMasterEntry(label, purity, "Gold Ornaments")));

    private static ItemMasterEntry Item(string name, decimal wastage) => new(
        Name: name,
        Unit: ItemUnits.Gram,
        WastagePercent: wastage,
        ItemCategory: ItemCategories.GoldBased,
        PricingMode: PricingModes.Wastage,
        DefaultLabourPerGram: 0m,
        DefaultStockMappingLabel: string.Empty);

    private static EffectiveSettingsResponse Response(string itemsJson, string karatsJson = "[]") =>
        TestSettings.Create("Shop", itemsJson, karatsJson);

    private sealed class FakeSettingsApi(EffectiveSettingsResponse response) : SettingsApiStub
    {
        public EffectiveSettingsResponse Response { get; set; } = response;
        public int GetCount { get; private set; }
        public int SaveCount { get; private set; }

        public override Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(Response);
        }

        public override Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(new SettingsUpdateResponse("cloud", "settings", ["masters"], DateTimeOffset.UtcNow));
        }
    }
}
