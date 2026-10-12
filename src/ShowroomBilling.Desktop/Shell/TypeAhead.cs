using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Suggestions while typing in an editable combo box, for a table cell such as the item or the karat:
/// <c>shell:TypeAhead.IsEnabled="True" shell:TypeAhead.Path="Name"</c>.
/// <para>
/// As the operator types, a small list opens under the cell with the entries whose text contains
/// every word typed, narrowing with each letter. Up and Down move through that list only while it is
/// open, Enter picks the highlighted entry, Tab keeps what was typed, Esc closes the list. The list is
/// the combo box template's own <c>PART_Suggest</c> popup, kept apart from the combo box's drop-down,
/// so moving through it never selects anything and never changes the text. When it is closed the
/// arrow keys are the table's: they move between rows (see <c>InvoiceView</c>).
/// </para>
/// Turn the combo box's own <c>IsTextSearchEnabled</c> off, or it completes the text in place as well.
/// </summary>
public static class TypeAhead
{
    private const int MaxSuggestions = 40;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TypeAhead), new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>The property of an entry whose text is matched; empty to match on the entry's own text.</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(TypeAhead), new PropertyMetadata(string.Empty));

    /// <summary>What the list shows now. Read by the template; set only by this class.</summary>
    public static readonly DependencyProperty SuggestionsProperty = DependencyProperty.RegisterAttached(
        "Suggestions", typeof(IEnumerable), typeof(TypeAhead), new PropertyMetadata(null));

    /// <summary>True while the list is open. Read by the template; set only by this class.</summary>
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(TypeAhead), new PropertyMetadata(false));

    /// <summary>Something drawn under the list, such as the keys that work in it.</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.RegisterAttached(
        "Footer", typeof(object), typeof(TypeAhead), new PropertyMetadata(null));

    /// <summary>The width the list is never narrower than; the cell's own width is the other floor.</summary>
    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.RegisterAttached(
        "MinWidth", typeof(double), typeof(TypeAhead), new PropertyMetadata(0d));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(TypeAhead), new PropertyMetadata(null));

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();

    static TypeAhead()
    {
        // A combo box steps through its entries on Up and Down in its own key handling, which runs before
        // any handler added to one instance. A class handler for the combo box type is the more derived
        // one, so it runs first, and marking the key handled keeps the combo box from selecting anything.
        EventManager.RegisterClassHandler(typeof(ComboBox), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
    }

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static string GetPath(DependencyObject element) => (string)element.GetValue(PathProperty);

    public static void SetPath(DependencyObject element, string value) => element.SetValue(PathProperty, value);

    public static IEnumerable? GetSuggestions(DependencyObject element) => (IEnumerable?)element.GetValue(SuggestionsProperty);

    public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);

    public static object? GetFooter(DependencyObject element) => element.GetValue(FooterProperty);

    public static void SetFooter(DependencyObject element, object? value) => element.SetValue(FooterProperty, value);

    public static double GetMinWidth(DependencyObject element) => (double)element.GetValue(MinWidthProperty);

    public static void SetMinWidth(DependencyObject element, double value) => element.SetValue(MinWidthProperty, value);

    private sealed class State
    {
        public TextBox? Box;
        public ListBox? List;

        /// <summary>The operator has typed (or deleted) since the last pick or since focus came here.</summary>
        public bool Typed;

        /// <summary>Text is changing because of a pick or a binding, not because of typing.</summary>
        public bool Quiet;
    }

    /// <summary>
    /// The entries whose text contains every word of <paramref name="typed"/>, ignoring case, the ones that
    /// start with the first word first, in their own order otherwise. Nothing typed gives nothing.
    /// </summary>
    public static IReadOnlyList<object> Filter(IEnumerable? entries, string? path, string? typed)
    {
        var words = (typed ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries is null || words.Length == 0)
        {
            return [];
        }

        return entries.Cast<object?>()
            .Where(entry => entry is not null)
            .Select(entry => (Entry: entry!, Text: TextOf(entry!, path)))
            .Where(pair => words.All(word => pair.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Text.StartsWith(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(pair => pair.Entry)
            .Take(MaxSuggestions)
            .ToArray();
    }

    /// <summary>The text of an entry: the named property, or the entry itself when no property is named.</summary>
    public static string TextOf(object entry, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return entry.ToString() ?? string.Empty;
        }

        var property = PropertyCache.GetOrAdd((entry.GetType(), path), key => key.Item1.GetProperty(key.Item2));
        return property?.GetValue(entry)?.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Enter on the cell: picks the highlighted entry and closes the list. False when the list was not
    /// open, so the caller does what Enter does anyway.
    /// </summary>
    public static bool TryPick(ComboBox combo)
    {
        if (!GetIsOpen(combo) || StateOf(combo) is not { List: { } list } state)
        {
            return false;
        }

        var picked = list.SelectedItem;
        Close(combo);
        if (picked is null)
        {
            return false;
        }

        Pick(combo, state, picked);
        return true;
    }

    /// <summary>Opens, narrows or closes the list for what is in the box now. The text box calls this as it is typed in.</summary>
    internal static void Refresh(ComboBox combo)
    {
        if (StateOf(combo) is not { } state || combo.IsDropDownOpen)
        {
            return;
        }

        var text = state.Box?.Text ?? combo.Text;
        var matches = Filter(combo.ItemsSource ?? combo.Items, GetPath(combo), text);
        if (matches.Count == 0)
        {
            Close(combo);
            return;
        }

        combo.SetValue(SuggestionsProperty, matches);
        combo.SetValue(IsOpenProperty, true);
        if (state.List is { } list)
        {
            list.SelectedIndex = 0;
            list.ScrollIntoView(list.SelectedItem);
        }
    }

    internal static void Close(ComboBox combo)
    {
        combo.SetValue(IsOpenProperty, false);
        combo.ClearValue(SuggestionsProperty);
    }

    private static State? StateOf(ComboBox combo) => (State?)combo.GetValue(StateProperty);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox combo)
        {
            return;
        }

        combo.Loaded -= OnLoaded;
        combo.Unloaded -= OnUnloaded;
        combo.LostKeyboardFocus -= OnLostKeyboardFocus;
        combo.GotKeyboardFocus -= OnGotKeyboardFocus;
        if (e.NewValue is not true)
        {
            return;
        }

        combo.SetValue(StateProperty, new State());
        combo.Loaded += OnLoaded;
        combo.Unloaded += OnUnloaded;
        combo.LostKeyboardFocus += OnLostKeyboardFocus;
        combo.GotKeyboardFocus += OnGotKeyboardFocus;
        if (combo.IsLoaded)
        {
            Attach(combo);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((ComboBox)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var combo = (ComboBox)sender;
        Close(combo);
        if (StateOf(combo) is { Box: { } box } state)
        {
            box.TextChanged -= OnTextChanged;
            box.PreviewTextInput -= OnPreviewTextInput;
            state.Box = null;
        }

        if (StateOf(combo) is { List: { } list } listState)
        {
            list.PreviewMouseLeftButtonUp -= OnListMouseUp;
            listState.List = null;
        }
    }

    private static void Attach(ComboBox combo)
    {
        if (StateOf(combo) is not { } state)
        {
            return;
        }

        combo.ApplyTemplate();
        if (state.Box is null && combo.Template.FindName("PART_EditableTextBox", combo) is TextBox box)
        {
            state.Box = box;
            box.TextChanged += OnTextChanged;
            box.PreviewTextInput += OnPreviewTextInput;
        }

        if (state.List is null && combo.Template.FindName("PART_SuggestList", combo) is ListBox list)
        {
            state.List = list;
            list.PreviewMouseLeftButtonUp += OnListMouseUp;
        }
    }

    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (StateOf((ComboBox)sender) is { } state)
        {
            state.Typed = false;
        }
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox { TemplatedParent: ComboBox combo } && StateOf(combo) is { } state)
        {
            state.Typed = true;
        }
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { TemplatedParent: ComboBox combo } || StateOf(combo) is not { } state || state.Quiet)
        {
            return;
        }

        // Backspace and Delete change the text without any text input, so they count as typing too.
        if (state.Typed || (Keyboard.IsKeyDown(Key.Back) || Keyboard.IsKeyDown(Key.Delete)) && combo.IsKeyboardFocusWithin)
        {
            state.Typed = true;
            Refresh(combo);
        }
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo || !GetIsEnabled(combo) || !GetIsOpen(combo) || StateOf(combo) is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None:
                MoveHighlight(combo, +1);
                e.Handled = true;
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None:
                MoveHighlight(combo, -1);
                e.Handled = true;
                break;
            case Key.Escape:
                Close(combo);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves the highlight of the open list one entry, and stops at either end. Nothing is selected and the text is not touched.</summary>
    internal static void MoveHighlight(ComboBox combo, int delta)
    {
        if (!GetIsOpen(combo) || StateOf(combo) is not { List: { } list })
        {
            return;
        }

        list.SelectedIndex = Math.Clamp(list.SelectedIndex + delta, 0, Math.Max(0, list.Items.Count - 1));
        list.ScrollIntoView(list.SelectedItem);
    }

    private static void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.TemplatedParent is not ComboBox combo || StateOf(combo) is not { } state
            || ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: { } picked })
        {
            return;
        }

        Close(combo);
        Pick(combo, state, picked);
        combo.Focus();
        e.Handled = true;
    }

    private static void Pick(ComboBox combo, State state, object picked)
    {
        state.Quiet = true;
        try
        {
            combo.SelectedItem = picked;
            // Setting the selection writes the entry into the box; this keeps it right when the entry has no text path.
            var text = TextOf(picked, GetPath(combo));
            if (state.Box is { } box && box.Text != text)
            {
                box.Text = text;
            }

            state.Box?.Select(state.Box.Text.Length, 0);
        }
        finally
        {
            state.Typed = false;
            combo.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => state.Quiet = false);
        }
    }

    private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var combo = (ComboBox)sender;
        if (combo.IsKeyboardFocusWithin)
        {
            return;
        }

        Close(combo);
        if (StateOf(combo) is not { Typed: true } state)
        {
            return;
        }

        // What was typed may be exactly an entry; if it is, the line is that item. If it is not, the
        // line is a free-text one and no longer the item it was.
        state.Typed = false;
        var text = (state.Box?.Text ?? combo.Text).Trim();
        var exact = (combo.ItemsSource ?? combo.Items).Cast<object?>()
            .FirstOrDefault(entry => entry is not null && string.Equals(TextOf(entry, GetPath(combo)), text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (!ReferenceEquals(combo.SelectedItem, exact))
            {
                Pick(combo, state, exact);
            }
        }
        else if (combo.SelectedItem is { } current
                 && !string.Equals(TextOf(current, GetPath(combo)), text, StringComparison.OrdinalIgnoreCase))
        {
            state.Quiet = true;
            try
            {
                combo.SelectedItem = null;
                if (state.Box is { } box && box.Text != text)
                {
                    box.Text = text;
                }
            }
            finally
            {
                combo.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => state.Quiet = false);
            }
        }
    }
}
