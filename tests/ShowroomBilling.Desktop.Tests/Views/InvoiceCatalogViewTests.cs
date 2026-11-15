using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.Views.Invoice;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The real Invoice screen when settings are saved or loaded under an open bill. The item and
/// karat cells are combo boxes: one drops its selection, and its text, when what it had selected
/// is no longer in its list, and writes that back to the line. A view-model test cannot see that.
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
            var view = new InvoiceView { DataContext = invoice };
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
            var itemBefore = CellOf(Combos(view, "ItemCombo")[0]);

            // Settings saved: the ring's default wastage and the karat's purity changed, the chain is gone.
            settings.Publish(
                items: [TestMasters.Item("Gold Ring", wastage: 10m)],
                karats: [TestMasters.Karat("22K", 92m)]);
            Layout(view);

            var items = Combos(view, "ItemCombo");
            var karats = Combos(view, "KaratCombo");
            var item = CellOf(items[0]);
            var karat = CellOf(karats[0]);
            var goneItem = CellOf(items[1]);
            var (itemName, karatLabel, wastage) = (ring.ItemName, ring.Karat, ring.WastagePercent);
            var (goneName, goneWastage) = (chain.ItemName, chain.WastagePercent);

            // The cells write their text to the line when the cursor leaves them.
            items[0].GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            items[1].GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
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
    public void Tab_reaches_the_karat_cell_straight_after_the_item_cell()
    {
        // Tab follows the order the cells are written in the line's template, not their columns.
        // The karat cell was written after the unit cell, so Tab went Item, Gross, Less, Net, Unit, Karat.
        var (item, karat, gross) = WpfTestHost.Run(() =>
        {
            var settings = OfflineApi.Settings();
            var invoice = new InvoiceViewModel(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings);
            var view = new InvoiceView { DataContext = invoice };
            Layout(view);

            var inOrder = Descendants(view).ToList();
            var itemAt = inOrder.FindIndex(element => element is ComboBox { Name: "ItemCombo" });
            var karatAt = inOrder.FindIndex(element => element is ComboBox { Name: "KaratCombo" });
            // The first text box after the item cell that is a cell of its own, not a combo box's inner box.
            var grossAt = inOrder.FindIndex(
                itemAt + 1,
                element => element is TextBox box && !IsInsideComboBox(box));
            return (itemAt, karatAt, grossAt);
        });

        Assert.True(item >= 0 && karat >= 0 && gross >= 0);
        Assert.True(item < karat, "the karat cell comes after the item cell");
        Assert.True(karat < gross, "the karat cell comes before the gross cell");
    }

    private static bool IsInsideComboBox(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ComboBox)
            {
                return true;
            }
        }

        return false;
    }

    private static Cell CellOf(ComboBox combo)
        => new(
            combo.SelectedItem switch
            {
                Contracts.Settings.ItemMasterEntry item => item.Name,
                Contracts.Settings.KaratMasterEntry karat => karat.Label,
                _ => null,
            },
            combo.Text);

    /// <summary>The cells of that name, one a line, from the first line down.</summary>
    private static List<ComboBox> Combos(DependencyObject root, string name)
        => Descendants(root).OfType<ComboBox>().Where(combo => combo.Name == name).ToList();

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
