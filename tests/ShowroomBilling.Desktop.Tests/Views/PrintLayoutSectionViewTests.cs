using System.Windows;
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
