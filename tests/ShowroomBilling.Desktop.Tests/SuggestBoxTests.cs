using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.Views;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The suggest box of the item and karat cells: what its list offers, how it is moved through, what
/// a pick does, and what it leaves alone.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class SuggestBoxTests
{
    private sealed record Entry(string Name);

    private static readonly Entry[] Items =
    [
        new("22kt Gold Chain (Rope, 18″)"),
        new("22kt Ladies Ring"),
        new("18kt Rose Gold Chain"),
        new("Diamond Solitaire Ring"),
        new("Plain Ring"),
    ];

    [Fact]
    public void Every_word_typed_must_be_in_the_name_whatever_the_case()
    {
        var found = SuggestBox.Filter(Items, "Name", "ring 22");

        Assert.Equal(["22kt Ladies Ring"], found.Cast<Entry>().Select(entry => entry.Name));
    }

    [Fact]
    public void Names_that_start_with_the_first_word_come_first_and_the_rest_keep_their_order()
    {
        var found = SuggestBox.Filter(Items, "Name", "ring");

        Assert.Equal(["22kt Ladies Ring", "Diamond Solitaire Ring", "Plain Ring"], found.Cast<Entry>().Select(entry => entry.Name));

        var chain = SuggestBox.Filter(Items, "Name", "gold").Cast<Entry>().Select(entry => entry.Name);
        Assert.Equal(["22kt Gold Chain (Rope, 18″)", "18kt Rose Gold Chain"], chain);

        var prefixFirst = SuggestBox.Filter(new[] { new Entry("Plain Ring"), new Entry("Ring Box") }, "Name", "ring");
        Assert.Equal(["Ring Box", "Plain Ring"], prefixFirst.Cast<Entry>().Select(entry => entry.Name));
    }

    [Fact]
    public void Nothing_typed_offers_nothing()
    {
        Assert.Empty(SuggestBox.Filter(Items, "Name", string.Empty));
        Assert.Empty(SuggestBox.Filter(Items, "Name", "   "));
        Assert.Empty(SuggestBox.Filter(null, "Name", "ring"));
    }

    [Fact]
    public void A_list_of_plain_text_is_matched_on_the_text_itself()
    {
        var found = SuggestBox.Filter(new[] { "gm", "ct" }, null, "c");

        Assert.Equal(["ct"], found.Cast<string>());
    }

    [Fact]
    public void Typing_opens_the_list_narrows_it_and_a_pick_selects_without_the_arrows_selecting_anything()
    {
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, list, window) = Hosted();

            box.Text = "r";
            var afterOne = (Open: suggest.IsListOpen, Count: list.Items.Count, Index: list.SelectedIndex);

            box.Text = "ri";
            box.Text = "rin";
            box.Text = "ring";
            var afterFour = (Open: suggest.IsListOpen, Count: list.Items.Count, suggest.Text);

            suggest.MoveHighlight(+1);
            suggest.MoveHighlight(+1);
            suggest.MoveHighlight(+1);
            var moved = (Index: list.SelectedIndex, Selected: suggest.SelectedItem, Text: box.Text);

            suggest.MoveHighlight(-1);
            var picked = suggest.TryPick();
            var after = (Picked: picked, Open: suggest.IsListOpen, Selected: suggest.SelectedItem as Entry, Box: box.Text, suggest.Text);

            box.Text = "zzz";
            var none = suggest.IsListOpen;
            window.Content = null;
            return (afterOne, afterFour, moved, after, none);
        });

        Assert.True(result.afterOne.Open);
        Assert.Equal(0, result.afterOne.Index);
        Assert.True(result.afterOne.Count >= 3);
        Assert.True(result.afterFour.Open);
        Assert.Equal(3, result.afterFour.Count);
        // What is typed is the box's text as it is typed.
        Assert.Equal("ring", result.afterFour.Text);

        // Moving through the list stops at the last entry, selects nothing and leaves the text as typed.
        Assert.Equal(2, result.moved.Index);
        Assert.Null(result.moved.Selected);
        Assert.Equal("ring", result.moved.Text);

        Assert.True(result.after.Picked);
        Assert.False(result.after.Open);
        Assert.Equal("Diamond Solitaire Ring", result.after.Selected?.Name);
        Assert.Equal("Diamond Solitaire Ring", result.after.Box);
        Assert.Equal("Diamond Solitaire Ring", result.after.Text);

        Assert.False(result.none);
    }

    [Fact]
    public void The_arrow_keys_move_the_highlight_only_while_the_list_is_open()
    {
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, list, window) = Hosted();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var source = System.Windows.Interop.HwndSource.FromHwnd(handle);

            KeyEventArgs Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                box.RaiseEvent(args);
                return args;
            }

            box.Text = "ring";

            var open = suggest.IsListOpen;
            var down = Press(Key.Down);
            var afterDown = (list.SelectedIndex, Selected: suggest.SelectedItem, box.Text, down.Handled);
            var up = Press(Key.Up);
            var afterUp = (list.SelectedIndex, Selected: suggest.SelectedItem, box.Text, up.Handled);

            // With the list closed the keys are not the box's: they are left for a table to move between rows.
            var escape = Press(Key.Escape);
            var closed = suggest.IsListOpen;
            var downClosed = Press(Key.Down);
            var escapeClosed = Press(Key.Escape);

            // Enter picks the highlighted entry; with nothing open it is not the box's either.
            box.Text = "plain";
            var enter = Press(Key.Enter);
            var afterEnter = (enter.Handled, Selected: (suggest.SelectedItem as Entry)?.Name, suggest.IsListOpen);
            var enterClosed = Press(Key.Enter);
            window.Content = null;
            return (open, afterDown, afterUp, EscapeHandled: escape.Handled, closed, DownClosed: downClosed.Handled, EscapeClosed: escapeClosed.Handled, afterEnter, EnterClosed: enterClosed.Handled);
        });

        Assert.True(result.open);
        Assert.Equal(1, result.afterDown.SelectedIndex);
        Assert.True(result.afterDown.Handled);
        Assert.Null(result.afterDown.Selected);
        Assert.Equal("ring", result.afterDown.Text);
        Assert.Equal(0, result.afterUp.SelectedIndex);
        Assert.Null(result.afterUp.Selected);
        Assert.Equal("ring", result.afterUp.Text);

        Assert.True(result.EscapeHandled);
        Assert.False(result.closed);
        Assert.False(result.DownClosed);
        Assert.False(result.EscapeClosed);

        Assert.Equal((true, "Plain Ring", false), result.afterEnter);
        Assert.False(result.EnterClosed);
    }

    [Fact]
    public void Leaving_the_box_makes_an_exact_name_that_entry_and_other_text_free_text()
    {
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, _, window) = Hosted();

            // The whole name, in another case, then Tab.
            box.Text = "plain ring";
            suggest.SettleTypedText();
            var exact = ((suggest.SelectedItem as Entry)?.Name, suggest.Text);

            // Typed over with something that is no entry.
            box.Text = "Plain Ring, engraved";
            suggest.SettleTypedText();
            var free = (suggest.SelectedItem, suggest.Text);

            // Nothing typed since the cursor came: leaving changes nothing.
            suggest.SelectedItem = Items[1];
            suggest.SettleTypedText();
            var untouched = ((suggest.SelectedItem as Entry)?.Name, suggest.Text);
            window.Content = null;
            return (exact, free, untouched);
        });

        Assert.Equal(("Plain Ring", "Plain Ring"), result.exact);
        Assert.Equal((null, "Plain Ring, engraved"), result.free);
        Assert.Equal(("22kt Ladies Ring", "22kt Ladies Ring"), result.untouched);
    }

    [Fact]
    public void The_selection_and_the_text_are_not_tied_to_the_list()
    {
        // Where this differs from an editable combo box: that one drops a selection which has left
        // its list, blanks the text with it and writes both back to whatever it is bound to.
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, _, window) = Hosted();
            suggest.SelectedItem = Items[4];
            var shown = (box.Text, suggest.Text);

            suggest.ItemsSource = new[] { new Entry("Something Else") };
            var afterSwap = ((suggest.SelectedItem as Entry)?.Name, box.Text);

            // A cleared selection leaves the text: free text is a value.
            suggest.SelectedItem = null;
            var afterClear = (suggest.SelectedItem, box.Text, suggest.Text);

            // A bill reopened with the name as it was typed keeps that spelling.
            suggest.Text = "plain ring";
            suggest.SelectedItem = Items[4];
            var otherCase = (box.Text, suggest.Text);
            window.Content = null;
            return (shown, afterSwap, afterClear, otherCase);
        });

        Assert.Equal(("Plain Ring", "Plain Ring"), result.shown);
        Assert.Equal(("Plain Ring", "Plain Ring"), result.afterSwap);
        Assert.Equal((null, "Plain Ring", "Plain Ring"), result.afterClear);
        Assert.Equal(("plain ring", "plain ring"), result.otherCase);
    }

    [Fact]
    public void The_chevron_opens_the_list_with_every_entry_and_the_selected_one_highlighted()
    {
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, list, window) = Hosted();
            suggest.SelectedItem = Items[3];

            // The chevron is a toggle bound to this.
            suggest.IsListOpen = true;
            var opened = (list.Items.Count, list.SelectedIndex, box.Text);

            suggest.IsListOpen = false;
            var closed = (suggest.IsListOpen, suggest.Suggestions is null);

            suggest.ItemsSource = Array.Empty<Entry>();
            suggest.IsListOpen = true;
            var nothingToShow = suggest.IsListOpen;
            window.Content = null;
            return (opened, closed, nothingToShow);
        });

        Assert.Equal((5, 3, "Diamond Solitaire Ring"), result.opened);
        Assert.Equal((false, true), result.closed);
        Assert.False(result.nothingToShow);
    }

    [Fact]
    public void A_row_of_the_list_is_drawn_by_the_boxs_template_or_shows_the_entrys_text()
    {
        // The list once had no row template of its own, so every entry read "Entry { Name = … }".
        var result = WpfTestHost.Run(() =>
        {
            var (plain, _, plainList, plainWindow) = Hosted();
            var byText = (plainList.DisplayMemberPath, HasTemplate: plainList.ItemTemplate is not null);
            plainWindow.Content = null;

            var rowTemplate = new DataTemplate();
            var (templated, _, templatedList, templatedWindow) = Hosted(rowTemplate);
            var byTemplate = (templatedList.DisplayMemberPath, Same: ReferenceEquals(templatedList.ItemTemplate, rowTemplate));
            templatedWindow.Content = null;
            return (byText, byTemplate);
        });

        Assert.Equal(("Name", false), result.byText);
        Assert.Equal((string.Empty, true), result.byTemplate);
    }

    [Fact]
    public void The_chevron_lies_over_the_end_of_the_text_box_so_a_click_on_it_reaches_it()
    {
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, _, window) = Hosted();
            var toggle = (FrameworkElement)suggest.Template.FindName("toggle", suggest);
            var siblings = ((Panel)toggle.Parent).Children;
            var seen = (Above: siblings.IndexOf(toggle) > siblings.IndexOf(box), toggle.Width, toggle.HorizontalAlignment);
            window.Content = null;
            return seen;
        });

        Assert.Equal((true, 30d, HorizontalAlignment.Right), result);
    }

    [Fact]
    public void A_shortcut_that_commits_the_text_settles_it_as_leaving_the_box_does()
    {
        // Ctrl+S with the cursor still in the cell: a name typed over an item must become that
        // item, not stay the old item under a new name.
        var result = WpfTestHost.Run(() =>
        {
            var (suggest, box, _, window) = Hosted();
            suggest.SelectedItem = Items[1];

            box.Text = "plain ring";
            suggest.CommitText();
            var exact = ((suggest.SelectedItem as Entry)?.Name, suggest.Text, suggest.IsListOpen);

            box.Text = "Repair charge";
            suggest.CommitText();
            var free = (suggest.SelectedItem, suggest.Text);
            window.Content = null;
            return (exact, free);
        });

        // The list that was open under the typing is closed with it.
        Assert.Equal(("Plain Ring", "Plain Ring", false), result.exact);
        Assert.Equal((null, "Repair charge"), result.free);
    }

    private static (SuggestBox Suggest, TextBox Box, ListBox List, Window Window) Hosted(DataTemplate? rowTemplate = null)
    {
        var suggest = new SuggestBox
        {
            TextPath = "Name",
            ItemsSource = Items,
            ItemTemplate = rowTemplate,
            Style = (Style)System.Windows.Application.Current.FindResource("TableCellSuggest"),
            Width = 300,
        };
        var window = new Window { Content = suggest };
        suggest.Measure(new Size(300, 40));
        suggest.Arrange(new Rect(0, 0, 300, 40));
        suggest.ApplyTemplate();
        var box = (TextBox)suggest.Template.FindName("PART_TextBox", suggest);
        var list = (ListBox)suggest.Template.FindName("PART_List", suggest);
        return (suggest, box, list, window);
    }
}
