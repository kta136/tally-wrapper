using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.Views;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>The suggestion list of the item and karat cells: what it offers, how it is moved through and what it leaves alone.</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class TypeAheadTests
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
        var found = TypeAhead.Filter(Items, "Name", "ring 22");

        Assert.Equal(["22kt Ladies Ring"], found.Cast<Entry>().Select(entry => entry.Name));
    }

    [Fact]
    public void Names_that_start_with_the_first_word_come_first_and_the_rest_keep_their_order()
    {
        var found = TypeAhead.Filter(Items, "Name", "ring");

        Assert.Equal(["22kt Ladies Ring", "Diamond Solitaire Ring", "Plain Ring"], found.Cast<Entry>().Select(entry => entry.Name));

        var chain = TypeAhead.Filter(Items, "Name", "gold").Cast<Entry>().Select(entry => entry.Name);
        Assert.Equal(["22kt Gold Chain (Rope, 18″)", "18kt Rose Gold Chain"], chain);

        var prefixFirst = TypeAhead.Filter(new[] { new Entry("Plain Ring"), new Entry("Ring Box") }, "Name", "ring");
        Assert.Equal(["Ring Box", "Plain Ring"], prefixFirst.Cast<Entry>().Select(entry => entry.Name));
    }

    [Fact]
    public void Nothing_typed_offers_nothing()
    {
        Assert.Empty(TypeAhead.Filter(Items, "Name", string.Empty));
        Assert.Empty(TypeAhead.Filter(Items, "Name", "   "));
        Assert.Empty(TypeAhead.Filter(null, "Name", "ring"));
    }

    [Fact]
    public void A_list_of_plain_text_is_matched_on_the_text_itself()
    {
        var found = TypeAhead.Filter(new[] { "gm", "ct" }, null, "c");

        Assert.Equal(["ct"], found.Cast<string>());
    }

    [Fact]
    public void Typing_opens_the_list_narrows_it_and_a_pick_selects_without_the_arrows_selecting_anything()
    {
        var result = WpfTestHost.Run(() =>
        {
            var combo = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = false,
                DisplayMemberPath = "Name",
                ItemsSource = Items,
                Style = (Style)System.Windows.Application.Current.FindResource("TableCellCombo"),
                Width = 300,
            };
            TypeAhead.SetPath(combo, "Name");
            TypeAhead.SetIsEnabled(combo, true);
            var window = new Window { Content = combo };
            combo.Measure(new Size(300, 40));
            combo.Arrange(new Rect(0, 0, 300, 40));
            combo.ApplyTemplate();
            combo.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var box = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
            var list = (ListBox)combo.Template.FindName("PART_SuggestList", combo);

            void Type(string text)
            {
                box.RaiseEvent(new TextCompositionEventArgs(
                    Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, text.Last().ToString()))
                { RoutedEvent = UIElement.PreviewTextInputEvent });
                box.Text = text;
            }

            Type("r");
            var afterOne = (Open: TypeAhead.GetIsOpen(combo), Count: list.Items.Count, Index: list.SelectedIndex);

            Type("ri");
            Type("rin");
            Type("ring");
            var afterFour = (Open: TypeAhead.GetIsOpen(combo), Count: list.Items.Count);

            TypeAhead.MoveHighlight(combo, +1);
            TypeAhead.MoveHighlight(combo, +1);
            TypeAhead.MoveHighlight(combo, +1);
            var moved = (Index: list.SelectedIndex, Selected: combo.SelectedItem, Text: box.Text);

            TypeAhead.MoveHighlight(combo, -1);
            var picked = TypeAhead.TryPick(combo);
            var after = (Picked: picked, Open: TypeAhead.GetIsOpen(combo), Selected: combo.SelectedItem as Entry, Text: box.Text);

            WpfTestHost.DrainDispatcher();
            Type("zzz");
            var none = TypeAhead.GetIsOpen(combo);
            window.Content = null;
            return (afterOne, afterFour, moved, after, none);
        });

        Assert.True(result.afterOne.Open);
        Assert.Equal(0, result.afterOne.Index);
        Assert.True(result.afterOne.Count >= 3);
        Assert.True(result.afterFour.Open);
        Assert.Equal(3, result.afterFour.Count);

        // Moving through the list stops at the last entry, selects nothing and leaves the text as typed.
        Assert.Equal(2, result.moved.Index);
        Assert.Null(result.moved.Selected);
        Assert.Equal("ring", result.moved.Text);

        Assert.True(result.after.Picked);
        Assert.False(result.after.Open);
        Assert.Equal("Diamond Solitaire Ring", result.after.Selected?.Name);
        Assert.Equal("Diamond Solitaire Ring", result.after.Text);

        Assert.False(result.none);
    }

    [Fact]
    public void The_arrow_keys_move_the_highlight_and_the_combo_box_does_not_step_through_its_entries()
    {
        var result = WpfTestHost.Run(() =>
        {
            var combo = new ComboBox
            {
                IsEditable = true,
                IsTextSearchEnabled = false,
                DisplayMemberPath = "Name",
                ItemsSource = Items,
                Style = (Style)System.Windows.Application.Current.FindResource("TableCellCombo"),
                Width = 300,
            };
            TypeAhead.SetPath(combo, "Name");
            TypeAhead.SetIsEnabled(combo, true);
            var window = new Window { Content = combo };
            var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var source = System.Windows.Interop.HwndSource.FromHwnd(handle);
            combo.Measure(new Size(300, 40));
            combo.Arrange(new Rect(0, 0, 300, 40));
            combo.ApplyTemplate();
            combo.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            var box = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
            var list = (ListBox)combo.Template.FindName("PART_SuggestList", combo);

            KeyEventArgs Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                box.RaiseEvent(args);
                return args;
            }

            box.RaiseEvent(new TextCompositionEventArgs(
                Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, "r"))
            { RoutedEvent = UIElement.PreviewTextInputEvent });
            box.Text = "ring";

            var open = TypeAhead.GetIsOpen(combo);
            var down = Press(Key.Down);
            var afterDown = (list.SelectedIndex, Selected: combo.SelectedItem, box.Text, down.Handled);
            var up = Press(Key.Up);
            var afterUp = (list.SelectedIndex, Selected: combo.SelectedItem, box.Text, up.Handled);

            // With the list closed the key is not this behaviour's: it is left for the table to move between rows.
            var escape = Press(Key.Escape);
            var closed = TypeAhead.GetIsOpen(combo);
            var downClosed = Press(Key.Down);
            window.Content = null;
            return (open, afterDown, afterUp, escape.Handled, closed, downClosed.Handled);
        });

        Assert.True(result.open);
        Assert.Equal(1, result.afterDown.SelectedIndex);
        Assert.True(result.afterDown.Handled);
        Assert.Null(result.afterDown.Selected);
        Assert.Equal("ring", result.afterDown.Text);
        Assert.Equal(0, result.afterUp.SelectedIndex);
        Assert.Null(result.afterUp.Selected);
        Assert.Equal("ring", result.afterUp.Text);

        Assert.True(result.Item4);
        Assert.False(result.closed);
    }
}
