using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// An item name that is not one of the saved items. It is allowed (a one-off piece), but it is also
/// what a name cut short by Ctrl+S looks like: "Gold Ri" was saved and printed as the item, priced
/// with the wastage of the item the line held before. So Save asks first.
/// </summary>
public sealed class InvoiceItemAsTypedTests
{
    [Fact]
    public async Task A_name_cut_short_over_a_saved_item_is_asked_about_and_no_sends_nothing()
    {
        var (invoice, api, dialogs, settings) = Invoice();
        var line = GoldLine(invoice, settings);
        // What the item cell does when Ctrl+S is pressed with "Gold Ri" typed over "Gold Ring":
        // the text goes to the line, then the item is let go.
        line.ItemName = "Gold Ri";
        line.ItemMaster = null;

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        var question = Assert.Single(dialogs.Questions);
        Assert.Equal("Save “Gold Ri” as typed?", question.Title);
        Assert.Equal(
            "“Gold Ri” on line 1 is not one of the saved items. The bill keeps it as typed, at the price the line shows.",
            question.Message);
        Assert.Equal(("Save as typed", true), (question.ConfirmText, question.IsWarning));
        Assert.Null(api.LastCreate);
        Assert.Equal("Not saved: “Gold Ri” on line 1 is not one of the saved items.", invoice.SaveStatus);
        // Nothing about the line was changed by asking.
        Assert.Equal(("Gold Ri", 8m), (line.ItemName, line.WastagePercent));

        // Picked from the list, as the question says: the sentence beside Save goes with it.
        line.ItemMaster = settings.Catalog.Items[0];
        Assert.Equal(string.Empty, invoice.SaveStatus);
        await invoice.SaveDraftCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Questions);
        Assert.NotNull(api.LastCreate);
    }

    [Fact]
    public async Task Save_as_typed_saves_the_name_as_it_was_typed()
    {
        var (invoice, api, dialogs, settings) = Invoice();
        dialogs.Confirm = _ => Task.FromResult(true);
        var line = GoldLine(invoice, settings);
        line.ItemName = "Hand-made pendant";
        line.ItemMaster = null;

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Questions);
        Assert.Equal("Hand-made pendant", Assert.Single(api.LastCreate!.Payload.Lines).ItemName);
    }

    [Fact]
    public async Task Several_lines_are_asked_about_at_once_and_listed()
    {
        var (invoice, api, dialogs, settings) = Invoice();
        var first = GoldLine(invoice, settings);
        first.ItemName = "Gold Ri";
        first.ItemMaster = null;
        if (!invoice.Lines.Any(candidate => candidate.IsEmpty))
        {
            invoice.AddRowCommand.Execute(null);
        }

        var second = invoice.Lines.First(candidate => candidate.IsEmpty);
        second.ItemMaster = settings.Catalog.Items[0];
        second.GrossWeight = 2m;
        second.KaratMaster = settings.Catalog.Karats[0];
        second.ItemName = "Gold";
        second.ItemMaster = null;

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        var question = Assert.Single(dialogs.Questions);
        Assert.Equal("Save 2 items as typed?", question.Title);
        Assert.Equal([("Line 1", "Gold Ri"), ("Line 2", "Gold")], question.Facts!.Select(f => (f.Label, f.Value)));
        Assert.Null(api.LastCreate);
        Assert.Equal("Not saved: 2 lines have an item that is not one of the saved items.", invoice.SaveStatus);
    }

    [Fact]
    public async Task A_saved_item_in_another_case_or_with_spaces_asks_nothing()
    {
        var (invoice, api, dialogs, settings) = Invoice();
        var line = GoldLine(invoice, settings);
        line.ItemMaster = null;   // the line takes the item back by its name …
        line.ItemName = " gold ring ";   // … and keeps the name as typed
        Assert.Equal(" gold ring ", line.ItemName);

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Questions);
        Assert.NotNull(api.LastCreate);
    }

    [Fact]
    public async Task A_bill_changed_while_the_question_was_open_is_not_saved()
    {
        // The question is an overlay: a row shortcut or settings arriving can change the bill
        // behind it. A yes is for the bill that was asked about.
        var (invoice, api, dialogs, settings) = Invoice();
        var line = GoldLine(invoice, settings);
        line.ItemName = "Gold Ri";
        line.ItemMaster = null;
        dialogs.Confirm = _ =>
        {
            line.GrossWeight = 5m;
            return Task.FromResult(true);
        };

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        Assert.Null(api.LastCreate);
        Assert.Equal("Not saved: the bill changed while the question was open. Check it and save again.", invoice.SaveStatus);

        // Asked again about the bill as it is now, and saved.
        dialogs.Confirm = _ => Task.FromResult(true);
        await invoice.SaveDraftCommand.ExecuteAsync(null);
        Assert.Equal(2, dialogs.Questions.Count);
        Assert.Equal(5m, Assert.Single(api.LastCreate!.Payload.Lines).GrossWeight);
    }

    [Fact]
    public async Task Removing_the_line_the_bill_was_refused_for_takes_the_sentence_away()
    {
        var (invoice, _, _, settings) = Invoice();
        var typo = GoldLine(invoice, settings);
        typo.ItemName = "Gold Ri";
        typo.ItemMaster = null;
        var good = invoice.Lines.First(candidate => candidate.IsEmpty);
        good.ItemMaster = settings.Catalog.Items[0];
        good.GrossWeight = 2m;
        good.KaratMaster = settings.Catalog.Karats[0];

        await invoice.SaveDraftCommand.ExecuteAsync(null);
        Assert.Equal("Not saved: “Gold Ri” on line 1 is not one of the saved items.", invoice.SaveStatus);

        invoice.RemoveLineCommand.Execute(typo);

        Assert.Equal(string.Empty, invoice.SaveStatus);
    }

    [Fact]
    public async Task With_no_items_saved_there_is_nothing_to_pick_from_and_nothing_is_asked()
    {
        var api = new FakeBillsApi();
        var dialogs = new FakeDialogs();
        var invoice = new InvoiceViewModel(api, FakeNumberingApi.Offline(), dialogs, null) { Rate24Kt = 10_000m, PartyName = "Walk-in" };
        invoice.Lines[0].ItemName = "Gold Ring";
        invoice.Lines[0].GrossWeight = 3m;
        invoice.Lines[0].Karat = "22K";

        await invoice.SaveDraftCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.Questions);
        Assert.NotNull(api.LastCreate);
    }

    private static BillLineViewModel GoldLine(InvoiceViewModel invoice, SettingsViewModel settings)
    {
        var line = invoice.Lines[0];
        line.ItemMaster = settings.Catalog.Items[0];
        line.GrossWeight = 3m;
        line.KaratMaster = settings.Catalog.Karats[0];
        return line;
    }

    private static (InvoiceViewModel Invoice, FakeBillsApi Api, FakeDialogs Dialogs, SettingsViewModel Settings) Invoice()
    {
        var api = new FakeBillsApi();
        var dialogs = new FakeDialogs();
        var settings = OfflineApi.Settings();
        settings.Publish(
            items: [TestMasters.Item("Gold Ring", wastage: 8m)],
            karats: [TestMasters.Karat("22K", 91.6m)]);
        var invoice = new InvoiceViewModel(api, FakeNumberingApi.Offline(), dialogs, settings)
        {
            Rate24Kt = 10_000m,
            PartyName = "Walk-in",
        };
        return (invoice, api, dialogs, settings);
    }
}
