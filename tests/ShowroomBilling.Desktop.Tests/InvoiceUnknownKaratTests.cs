using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.Tests.Views;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// A karat typed on a gold line that is none of the saved karats ("20K" for "22K"). The line has
/// no purity, so it was priced with no gold in it, and the bill was saved that way without a word.
/// </summary>
public sealed class InvoiceUnknownKaratTests
{
    [Fact]
    public async Task A_karat_that_is_not_a_saved_karat_marks_the_line_and_the_bill_is_not_saved()
    {
        var (invoice, api, settings) = Invoice();
        var line = invoice.Lines[0];
        line.ItemMaster = settings.Catalog.Items[0];
        line.GrossWeight = 3m;
        line.Karat = "20K";   // typed, and no such karat

        Assert.True(line.KaratUnknown);
        Assert.Equal("Line 1: “20K” is not one of the karats. Pick one from the list.", invoice.UnknownKaratProblem());
        Assert.Equal("“20K” is not one of the karats. Pick one from the list.", invoice.FocusExplanation?.Problem);

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        Assert.Null(api.LastCreate);
        Assert.Equal("Line 1: “20K” is not one of the karats. Pick one from the list.", invoice.SaveStatus);

        // Another edit that leaves the fault: the sentence beside Save still stands.
        line.GrossWeight = 4m;
        Assert.Equal("Line 1: “20K” is not one of the karats. Pick one from the list.", invoice.SaveStatus);
        line.GrossWeight = 3m;

        // Corrected from the list: priced with its gold, the sentence gone without another Save, and saved.
        line.KaratMaster = settings.Catalog.Karats[0];
        Assert.False(line.KaratUnknown);
        Assert.Null(invoice.UnknownKaratProblem());
        Assert.Equal(string.Empty, invoice.SaveStatus);
        Assert.Equal("Draft, not saved", invoice.StatusText);
        // 3 g at 22K 9,160 and 8 % wastage 800: with its gold in it.
        Assert.Equal(29_880m, line.LineTotal);

        await invoice.SaveDraftCommand.ExecuteAsync(null);
        Assert.NotNull(api.LastCreate);
    }

    [Fact]
    public void A_saved_karat_typed_in_another_case_or_with_spaces_is_known()
    {
        var (invoice, _, settings) = Invoice();
        var line = invoice.Lines[0];
        line.ItemMaster = settings.Catalog.Items[0];
        line.GrossWeight = 3m;

        line.Karat = " 22k ";
        Assert.False(line.KaratUnknown);

        // Nothing typed is the other marker's business (the red "Karat" in the empty cell).
        line.Karat = string.Empty;
        Assert.False(line.KaratUnknown);
        Assert.True(line.NeedsKarat);
    }

    [Fact]
    public void A_diamond_line_and_a_shop_with_no_karats_saved_are_left_alone()
    {
        var (invoice, _, settings) = Invoice();
        settings.Publish(items: [TestMasters.Item("Diamond Ring", ItemUnits.Carat, category: ItemCategories.Diamond)]);
        var diamond = invoice.Lines[0];
        diamond.ItemMaster = settings.Catalog.Items[0];
        diamond.GrossWeight = 1m;
        Assert.False(diamond.KaratUnknown);

        // No karats to hold the text against: the line is as it always was.
        var bare = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), null) { Rate24Kt = 10_000m };
        bare.Lines[0].ItemName = "Gold Ring";
        bare.Lines[0].GrossWeight = 3m;
        bare.Lines[0].Karat = "20K";
        Assert.False(bare.Lines[0].KaratUnknown);
        Assert.Null(bare.UnknownKaratProblem());
    }

    [Fact]
    public async Task A_bill_opened_before_the_karats_had_loaded_knows_its_karats_once_they_have()
    {
        var billId = Guid.NewGuid();
        var payload = new BillPayloadDto(
            PartyName: "Customer",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: new DateOnly(2026, 4, 24),
            Lines:
            [
                new BillLineItemDto(
                    ItemName: "Gold Ring",
                    HsnCode: "7113",
                    Quantity: 3m,
                    Unit: ItemUnits.Gram,
                    Rate: 9_960m,
                    LineTotal: 29_880m,
                    Karat: "22K",
                    RawJson: null,
                    StockName: "Gold Ornaments 22KT",
                    GrossWeight: 3m,
                    LessWeight: null,
                    WastagePercent: 8m,
                    LabourPerUnit: 0m,
                    DiamondRate: null,
                    Extra: null,
                    ItemCategory: ItemCategories.GoldBased,
                    PricingMode: PricingModes.Wastage),
            ],
            Totals: new BillTotalsDto(29_009.71m, 0m, 870.29m, 0m, 29_880m),
            Notes: null,
            Payment: "Cash",
            Rate24Kt: 10_000m);
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId, BillStates.Pending, payload) };
        var settings = OfflineApi.Settings();   // nothing loaded yet
        var invoice = new InvoiceViewModel(api, FakeNumberingApi.Offline(), new FakeDialogs(), settings);

        await invoice.LoadBillForEditAsync(billId);
        var line = invoice.Lines[0];
        Assert.Null(line.KaratMaster);
        Assert.False(line.KaratUnknown);

        // The settings arrive: the line's "22K" is the saved karat, not an unknown one.
        settings.Publish(
            items: [TestMasters.Item("Gold Ring", wastage: 8m)],
            karats: [TestMasters.Karat("22K", 91.6m), TestMasters.Karat("18K", 75m)]);

        Assert.Same(settings.Catalog.Karats[0], line.KaratMaster);
        Assert.False(line.KaratUnknown);
        Assert.Equal(8m, line.WastagePercent);
    }

    private static (InvoiceViewModel Invoice, FakeBillsApi Api, SettingsViewModel Settings) Invoice()
    {
        var api = new FakeBillsApi();
        var settings = OfflineApi.Settings();
        settings.Publish(
            items: [TestMasters.Item("Gold Ring", wastage: 8m)],
            karats: [TestMasters.Karat("22K", 91.6m), TestMasters.Karat("18K", 75m)]);
        var invoice = new InvoiceViewModel(api, FakeNumberingApi.Offline(), new FakeDialogs(), settings)
        {
            Rate24Kt = 10_000m,
            PartyName = "Walk-in",
        };
        return (invoice, api, settings);
    }
}

/// <summary>Print estimate is the shell's, so the refusal is checked through it.</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class EstimateWithUnknownKaratTests
{
    [Fact]
    public void An_estimate_is_not_previewed_while_a_line_has_a_karat_that_is_not_a_saved_karat()
    {
        var (dialogWithTypo, status, problemOnceCorrected) = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell();
            shell.ConfirmDialog.Cancel();
            shell.ActiveTab = NavTab.Invoice;
            var line = shell.Invoice.Lines.First(candidate => candidate is { IsEmpty: false, IsGoldLine: true });
            var saved = line.KaratMaster;

            line.KaratMaster = null;
            line.Karat = "20K";
            shell.PrintEstimateCommand.Execute(null);
            var withTypo = (shell.ActiveDialog, shell.Invoice.SaveStatus);

            // Corrected, nothing stands in the estimate's way. (The sample shell has no printer
            // behind it, so the preview itself is not opened here.)
            line.KaratMaster = saved;
            return (withTypo.ActiveDialog, withTypo.SaveStatus, shell.Invoice.UnknownKaratProblem());
        });

        Assert.Null(dialogWithTypo);
        Assert.EndsWith("“20K” is not one of the karats. Pick one from the list.", status);
        Assert.Null(problemOnceCorrected);
    }
}
