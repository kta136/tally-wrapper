using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.Views.Bills;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The real Bills screen: what the view model says must be on it. Its status text was set in a
/// dozen places and bound nowhere, and two toolbar buttons were handed a row that was never
/// announced, so they could not be pressed. A view-model test saw neither.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class BillsViewTests
{
    private static readonly Size ViewSize = new(1280, 800);

    private sealed record Seen(
        bool EditBeforeTick,
        bool EditAfterTick,
        bool HintBeforeAction,
        string FooterAfterRetry,
        string FooterAfterRefusal,
        bool HintAfterRefusal,
        IReadOnlyList<string> StateFilter);

    [Fact]
    public void The_toolbar_edit_follows_the_ticked_row_and_the_footer_says_what_an_action_came_to()
    {
        var seen = WpfTestHost.Run(() =>
        {
            var api = new FakeBillsApi
            {
                Response = new BillListResponse(1, 0, 50,
                [
                    new BillSummaryItem(
                        Id: Guid.NewGuid(),
                        State: BillStates.Failed,
                        InvoiceNumber: "SR/25-26/0001",
                        PartyName: "Walk-in",
                        BillDate: new DateOnly(2026, 4, 25),
                        GrandTotal: 100m,
                        CreatedAtUtc: DateTimeOffset.UtcNow,
                        UpdatedAtUtc: DateTimeOffset.UtcNow),
                ]),
            };
            var vm = new BillsViewModel(api, new FakeDialogs());
            vm.ApplyTallyHealthSnapshot(new SystemHealthSnapshot(
                ApiReachable: true,
                Masters: null,
                TallyCompany: new TallyCompanyHealthResponse(
                    TallyHealthStatuses.Healthy, true, "Test Company", true, 1, DateTimeOffset.UtcNow, "ok"),
                Runtime: null));
            vm.LoadAsync().GetAwaiter().GetResult();
            var view = new BillsView { DataContext = vm };
            Layout(view);

            var edit = Buttons(view).Single(button => button.Content as string == "Edit");
            var editBefore = edit.IsEnabled;
            var hintBefore = IsShown(Hint(view));

            vm.Items[0].IsSelected = true;
            Layout(view);
            var editAfter = edit.IsEnabled;

            vm.RetryRowCommand.ExecuteAsync(vm.Items[0]).GetAwaiter().GetResult();
            Layout(view);
            var afterRetry = FooterNotice(view);

            api.FailWith = new ApiException(HttpStatusCode.Conflict, "Conflict", "Bill is already posted.", null);
            vm.RetryRowCommand.ExecuteAsync(vm.Items[0]).GetAwaiter().GetResult();
            Layout(view);

            var filter = Descendants(view).OfType<ComboBox>().First(combo => ReferenceEquals(combo.ItemsSource, vm.StateFilterOptions));
            // The drop-down is a popup, which a view in no window cannot open: word each option
            // through the template the list gives its entries.
            var options = filter.Items.Cast<object>()
                .Select(item =>
                {
                    var entry = (FrameworkElement)filter.ItemTemplate.LoadContent();
                    entry.DataContext = item;
                    entry.Measure(ViewSize);
                    WpfTestHost.DrainDispatcher();
                    return ((TextBlock)entry).Text;
                })
                .ToList();

            return new Seen(editBefore, editAfter, hintBefore, afterRetry, FooterNotice(view), IsShown(Hint(view)), options);
        });

        // Nothing ticked: nothing to edit. One bill ticked: the toolbar's Edit can be pressed.
        Assert.False(seen.EditBeforeTick);
        Assert.True(seen.EditAfterTick);

        // The footer keeps its hint until there is something to say, then says it.
        Assert.True(seen.HintBeforeAction);
        Assert.Equal("Retried SR/25-26/0001.", seen.FooterAfterRetry);
        Assert.Equal("Retry failed: Bill is already posted.", seen.FooterAfterRefusal);
        Assert.False(seen.HintAfterRefusal);

        // The state filter words the states as the chips do.
        Assert.Contains("Reconcile", seen.StateFilter);
        Assert.DoesNotContain(BillStates.ReconciliationRequired, seen.StateFilter);
    }

    private static string FooterNotice(DependencyObject view)
    {
        var text = Descendants(view).OfType<TextBlock>().Single(block => block.Name == "FooterNoticeText");
        return IsShown(text) ? text.Text : string.Empty;
    }

    /// <summary>
    /// Whether the element and everything above it is set to show. <c>IsVisible</c> cannot be
    /// asked here: it is false for a view that is in no window.
    /// </summary>
    private static bool IsShown(DependencyObject? element)
    {
        for (; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static TextBlock Hint(DependencyObject view)
        => Descendants(view).OfType<TextBlock>().Single(block => block.Text == "Right-click a row for more actions");

    private static IEnumerable<Button> Buttons(DependencyObject view) => Descendants(view).OfType<Button>();

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
