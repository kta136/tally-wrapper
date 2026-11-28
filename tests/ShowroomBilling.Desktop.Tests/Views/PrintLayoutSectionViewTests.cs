using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.Views.Settings;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The real Settings → Print layout section. Its status line is the only place a failed upload,
/// delete or save is said, and it was once dropped from the view while the view model went on
/// writing it. ViewSmokeTests cannot see a line that is not there.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class PrintLayoutSectionViewTests
{
    private static readonly Size ViewSize = new(1280, 800);

    [Fact]
    public void The_section_shows_what_the_last_action_came_to()
    {
        const string failure = "Upload failed: the server refused the file.";

        var shown = WpfTestHost.Run(() =>
        {
            var (view, layout) = Section();
            layout.StatusMessage = failure;
            WpfTestHost.Layout(view, ViewSize);
            return Texts(view).Contains(failure);
        });

        Assert.True(shown);
    }

    [Fact]
    public void An_image_card_is_no_tab_stop_of_its_own_and_says_what_its_position_is_measured_from()
    {
        var (cards, labels) = WpfTestHost.Run(() =>
        {
            var (view, _) = Section();
            WpfTestHost.Layout(view, ViewSize);
            var found = Descendants(view).OfType<ContentControl>()
                .Where(control => control.Content is PrintLayoutAssetViewModel)
                .Select(control => control.Focusable)
                .ToList();
            return (found, Texts(view).Where(text => text.EndsWith("X, cm", StringComparison.Ordinal)).Order().ToList());
        });

        Assert.Equal([false, false, false], cards);
        // The logo and the signature are moved within their own place; only the watermark is placed on the page.
        Assert.Equal(["Offset X, cm", "Offset X, cm", "X, cm"], labels);
    }

    [Fact]
    public void Every_row_of_the_sections_table_keeps_its_buttons_inside_the_card()
    {
        var (rows, widerThanList, overhang) = WpfTestHost.Run(() =>
        {
            var (view, layout) = Section();
            // Every row pinned: a name with its Pinned chip is the widest a row's first cell gets,
            // and such rows once made the whole list wider than the card.
            layout.PinBottomFromHereCommand.Execute(layout.SectionLayouts[0]);
            // The width the card has beside the preview in a 1280 px window.
            WpfTestHost.Layout(view, new Size(545, 1600), passes: 3);
            ViewSnapshot.Save(view, "PrintLayout-Sections-AllPinned");

            var list = Descendants(view).OfType<ListBox>().Single(box => box.Name == "SectionList");
            var wider = Descendants(list).OfType<ListBoxItem>()
                .Select(row => row.ActualWidth - list.ActualWidth)
                .ToList();
            var past = Descendants(list).OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) == "Pin from here")
                .Select(button => button.TransformToAncestor(list).TransformBounds(new Rect(button.RenderSize)).Right - list.ActualWidth)
                .ToList();
            return (layout.SectionLayouts.Count, wider, past);
        });

        Assert.Equal(rows, widerThanList.Count);
        Assert.All(widerThanList, wider => Assert.True(wider <= 0.5, $"A row is {wider:0.#} px wider than the list, so its columns leave the headings'."));
        Assert.Equal(rows, overhang.Count);
        Assert.All(overhang, past => Assert.True(past <= 0, $"A row's last button ends {past:0.#} px past the list."));
    }

    private static (PrintLayoutSettingsSectionView View, PrintLayoutViewModel Layout) Section()
    {
        var layout = DesignSample.CreateSettings().PrintLayout;
        return (new PrintLayoutSettingsSectionView { DataContext = layout }, layout);
    }

    private static List<string> Texts(DependencyObject view) =>
        Descendants(view).OfType<TextBlock>().Select(block => block.Text).ToList();

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
}
