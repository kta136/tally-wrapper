using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.Views.Invoice;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The real Invoice screen when settings are saved or loaded under an open bill: every cell must
/// go on showing what its line holds. The item and karat cells were editable combo boxes, which
/// drop a selection that has left their list, blank the text with it and write both back to the
/// line; a view-model test cannot see that, so this drives the screen.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class InvoiceCatalogViewTests
{
    private static readonly Size ViewSize = new(1280, 800);

    private sealed record Cell(string? Selected, string Text);

    private sealed record Seen(
        Cell ItemBefore,
        Cell Item,
        Cell Karat,
        Cell GoneItem,
        string ItemName,
        string KaratLabel,
        decimal? Wastage,
        bool OnNewItem,
        bool OnNewKarat,
        string GoneItemName,
        decimal? GoneWastage,
        bool GoneItemLeft,
        string ItemNameAfterLeavingTheCell,
        string GoneItemNameAfterLeavingTheCell);

    [Fact]
    public void A_new_catalogue_under_an_open_bill_leaves_every_cell_showing_what_the_line_holds()
    {
        var seen = WpfTestHost.Run(() =>
        {
            var settings = OfflineApi.Settings();
            settings.Publish(
                items: [TestMasters.Item("Gold Ring", wastage: 8m), TestMasters.Item("Gold Chain", wastage: 6m)],
                karats: [TestMasters.Karat("22K", 91.6m)]);
            var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings)
            {
                Rate24Kt = 10_000m,
            };
            var view = Hosted(invoice);
            Layout(view);

            var ring = invoice.Lines[0];
            ring.ItemMaster = settings.Catalog.Items[0];
            ring.KaratMaster = settings.Catalog.Karats[0];
            ring.GrossWeight = 10m;
            ring.WastagePercent = 5m;   // typed over the item's 8 %
            var chain = invoice.Lines[1];
            chain.ItemMaster = settings.Catalog.Items[1];
            chain.KaratMaster = settings.Catalog.Karats[0];
            chain.GrossWeight = 4m;
            chain.WastagePercent = 4m;
            Layout(view);
            var itemBefore = CellOf(Cells(view, "ItemBox")[0]);

            // Settings saved: the ring's default wastage and the karat's purity changed, the chain is gone.
            settings.Publish(
                items: [TestMasters.Item("Gold Ring", wastage: 10m)],
                karats: [TestMasters.Karat("22K", 92m)]);
            Layout(view);

            var items = Cells(view, "ItemBox");
            var karats = Cells(view, "KaratBox");
            var item = CellOf(items[0]);
            var karat = CellOf(karats[0]);
            var goneItem = CellOf(items[1]);
            var (itemName, karatLabel, wastage) = (ring.ItemName, ring.Karat, ring.WastagePercent);
            var (goneName, goneWastage) = (chain.ItemName, chain.WastagePercent);

            // The cells write their text to the line when the cursor leaves them.
            items[0].CommitText();
            items[1].CommitText();
            WpfTestHost.DrainDispatcher();

            return new Seen(
                itemBefore,
                item,
                karat,
                goneItem,
                itemName,
                karatLabel,
                wastage,
                ReferenceEquals(ring.ItemMaster, settings.Catalog.Items[0]),
                ReferenceEquals(ring.KaratMaster, settings.Catalog.Karats[0]),
                goneName,
                goneWastage,
                chain.ItemMaster is null,
                ring.ItemName,
                chain.ItemName);
        });

        Assert.Equal(new Cell("Gold Ring", "Gold Ring"), seen.ItemBefore);

        // The ring's line is on the item and karat as they are now, and shows them, …
        Assert.True(seen.OnNewItem);
        Assert.True(seen.OnNewKarat);
        Assert.Equal(new Cell("Gold Ring", "Gold Ring"), seen.Item);
        Assert.Equal(new Cell("22K", "22K"), seen.Karat);
        // … with what was typed on it.
        Assert.Equal(("Gold Ring", "22K", 5m), (seen.ItemName, seen.KaratLabel, seen.Wastage));

        // The chain is no longer an item: its line keeps its name and its values, and the cell shows the name.
        Assert.True(seen.GoneItemLeft);
        Assert.Equal(new Cell(null, "Gold Chain"), seen.GoneItem);
        Assert.Equal(("Gold Chain", 4m), (seen.GoneItemName, seen.GoneWastage));

        // Nothing is lost when the cursor next passes through the cells.
        Assert.Equal("Gold Ring", seen.ItemNameAfterLeavingTheCell);
        Assert.Equal("Gold Chain", seen.GoneItemNameAfterLeavingTheCell);
    }

    [Fact]
    public void A_bill_that_is_there_before_the_screen_is_built_shows_its_items_and_karats_by_name()
    {
        // The cells get their selection, their text and the property that names an entry's text in
        // the order the markup lists them. A cell that showed its selection before it knew that
        // property showed "ItemMasterEntry { Name = … }", and would have written it to the line.
        var seen = WpfTestHost.Run(() =>
        {
            var settings = OfflineApi.Settings();
            settings.Publish(
                items: [TestMasters.Item("Gold Ring", wastage: 8m)],
                karats: [TestMasters.Karat("22K", 91.6m)]);
            var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings)
            {
                Rate24Kt = 10_000m,
            };
            var line = invoice.Lines[0];
            line.ItemMaster = settings.Catalog.Items[0];
            line.KaratMaster = settings.Catalog.Karats[0];
            line.GrossWeight = 10m;

            var view = Hosted(invoice);
            Layout(view);
            var item = Cells(view, "ItemBox")[0];
            var karat = Cells(view, "KaratBox")[0];
            var shown = (Item: CellOf(item), ItemBox: BoxOf(item).Text, Karat: CellOf(karat), KaratBox: BoxOf(karat).Text);

            // Nothing was typed, so passing through the cells writes nothing new to the line.
            item.CommitText();
            karat.CommitText();
            WpfTestHost.DrainDispatcher();
            return (shown, line.ItemName, line.Karat);
        });

        Assert.Equal(new Cell("Gold Ring", "Gold Ring"), seen.shown.Item);
        Assert.Equal("Gold Ring", seen.shown.ItemBox);
        Assert.Equal(new Cell("22K", "22K"), seen.shown.Karat);
        Assert.Equal("22K", seen.shown.KaratBox);
        Assert.Equal(("Gold Ring", "22K"), (seen.ItemName, seen.Karat));
    }

    [Fact]
    public void The_sample_bill_shows_every_item_and_karat_by_name()
    {
        // The same thing on the sample the screen pictures are taken from, where it first showed.
        var seen = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell();
            var view = Hosted(shell.Invoice);
            Layout(view);
            var lines = shell.Invoice.Lines.Where(line => !line.IsEmpty).ToList();
            var items = Cells(view, "ItemBox");
            var karats = Cells(view, "KaratBox");
            return lines.Select((line, index) => (
                Line: (line.ItemName, line.Karat),
                // The cell's own text: a diamond line's karat cell is hidden and has no inner box yet.
                Cells: (items[index].Text, karats[index].Text))).ToList();
        });

        Assert.NotEmpty(seen);
        Assert.All(seen, row => Assert.Equal(row.Line, row.Cells));
    }

    [Fact]
    public void The_item_cells_list_draws_each_item_with_its_summary_and_the_karat_cells_shows_the_label()
    {
        var seen = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell();
            var view = Hosted(shell.Invoice);
            Layout(view);
            var itemList = (ListBox)Cells(view, "ItemBox")[0].Template.FindName("PART_List", Cells(view, "ItemBox")[0]);
            var karatList = (ListBox)Cells(view, "KaratBox")[0].Template.FindName("PART_List", Cells(view, "KaratBox")[0]);
            return (
                ItemRow: itemList.ItemTemplate is not null && ReferenceEquals(itemList.ItemTemplate, view.FindResource("ItemPickerRow")),
                KaratPath: karatList.DisplayMemberPath);
        });

        Assert.True(seen.ItemRow);
        Assert.Equal("Label", seen.KaratPath);
    }

    private static TextBox BoxOf(SuggestBox cell) => (TextBox)cell.Template.FindName("PART_TextBox", cell);

    /// <summary>
    /// The screen as the app has it: inside a window (never shown). In no window at all, a row's
    /// cells receive their values in another order, and what went wrong on the real screen did not
    /// go wrong in the test.
    /// </summary>
    private static InvoiceView Hosted(InvoiceViewModel invoice)
    {
        var view = new InvoiceView { DataContext = invoice };
        _ = new Window { Content = view };
        return view;
    }

    [Fact]
    public void A_long_item_name_stays_inside_its_cell()
    {
        // A name longer than the column: the cell must stay the column's width and scroll its text,
        // or its chevron and the end of the name end up out of sight under the next column.
        var (cell, column, otherRow) = WpfTestHost.Run(() =>
        {
            var settings = OfflineApi.Settings();
            var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings);
            invoice.Lines[0].ItemName = "Repair of an old chain with a new clasp, a new hook and a length of new links";
            invoice.Lines[1].ItemName = "Ring";
            var view = Hosted(invoice);
            Layout(view);

            var cells = Cells(view, "ItemBox");
            var border = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(cells[0])));
            return (cells[0].ActualWidth, border.ActualWidth, cells[1].ActualWidth);
        });

        Assert.True(column > 0);
        Assert.True(cell <= column, $"the cell is {cell} wide in a column of {column}");
        Assert.Equal(otherRow, cell);
    }

    [Fact]
    public void Tab_reaches_the_karat_cell_straight_after_the_item_cell()
    {
        // Tab follows the order the cells are written in the line's template, not their columns.
        // The karat cell was written after the unit cell, so Tab went Item, Gross, Less, Net, Unit, Karat.
        var (item, karat, gross) = WpfTestHost.Run(() =>
        {
            var settings = OfflineApi.Settings();
            var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings);
            var view = Hosted(invoice);
            Layout(view);

            var inOrder = Descendants(view).ToList();
            var itemAt = inOrder.FindIndex(element => element is SuggestBox { Name: "ItemBox" });
            var karatAt = inOrder.FindIndex(element => element is SuggestBox { Name: "KaratBox" });
            // The first text box after the item cell that is a cell of its own, not a suggest box's inner box.
            var grossAt = inOrder.FindIndex(
                itemAt + 1,
                element => element is TextBox box && !IsInsideSuggestBox(box));
            return (itemAt, karatAt, grossAt);
        });

        Assert.True(item >= 0 && karat >= 0 && gross >= 0);
        Assert.True(item < karat, "the karat cell comes after the item cell");
        Assert.True(karat < gross, "the karat cell comes before the gross cell");
    }

    private static bool IsInsideSuggestBox(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is SuggestBox)
            {
                return true;
            }
        }

        return false;
    }

    private static Cell CellOf(SuggestBox combo)
        => new(
            combo.SelectedItem switch
            {
                Contracts.Settings.ItemMasterEntry item => item.Name,
                Contracts.Settings.KaratMasterEntry karat => karat.Label,
                _ => null,
            },
            combo.Text);

    /// <summary>The cells of that name, one a line, from the first line down.</summary>
    private static List<SuggestBox> Cells(DependencyObject root, string name)
        => Descendants(root).OfType<SuggestBox>().Where(cell => cell.Name == name).ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(ViewSize);
            view.Arrange(new Rect(ViewSize));
            view.UpdateLayout();
            WpfTestHost.DrainDispatcher();
        }
    }
}
