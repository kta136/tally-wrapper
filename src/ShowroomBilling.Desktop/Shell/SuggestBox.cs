using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// A text box that suggests entries of a list while it is typed in: the item and the karat cell of
/// the invoice's line table.
/// <para>
/// As the operator types, a list opens under the box with the entries whose text contains every
/// word typed, narrowing with each letter. Up and Down move through the list only while it is open
/// (they pick nothing and never change the text), Enter picks the highlighted entry, Esc closes the
/// list, and Tab keeps what was typed: free text is a valid value. The chevron, or Alt+Down, opens
/// the list with every entry. While the list is closed the arrow keys are not handled here, so a
/// table can use them to move between rows.
/// </para>
/// <para>
/// <see cref="Text"/> is what the box holds and <see cref="SelectedItem"/> is the entry it stands
/// for, or null for free text. On leaving the box, text that is exactly an entry's becomes that
/// entry, and text that is no longer the selected entry's clears the selection. The selection is
/// not tied to <see cref="ItemsSource"/>: swapping the list for another neither clears it nor
/// touches the text, which is where this differs from an editable combo box.
/// </para>
/// </summary>
[TemplatePart(Name = BoxPart, Type = typeof(TextBox))]
[TemplatePart(Name = ListPart, Type = typeof(ListBox))]
public sealed class SuggestBox : Control
{
    private const string BoxPart = "PART_TextBox";
    private const string ListPart = "PART_List";
    private const int MaxSuggestions = 40;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SuggestBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((SuggestBox)d).OnTextSet((string?)e.NewValue),
            null,
            isAnimationProhibited: true,
            System.Windows.Data.UpdateSourceTrigger.LostFocus));

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem),
        typeof(object),
        typeof(SuggestBox),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((SuggestBox)d).OnSelectedItemSet(e.NewValue)));

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(SuggestBox), new PropertyMetadata(null));

    /// <summary>The property of an entry whose text is matched and shown; empty to use the entry's own text.</summary>
    public static readonly DependencyProperty TextPathProperty = DependencyProperty.Register(
        nameof(TextPath),
        typeof(string),
        typeof(SuggestBox),
        new PropertyMetadata(string.Empty, (d, _) => ((SuggestBox)d).OnTextPathSet()));

    /// <summary>How a row of the list is drawn; without one a row shows the entry's text.</summary>
    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(SuggestBox), new PropertyMetadata(null));

    /// <summary>Something drawn under the list, such as the keys that work in it.</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(SuggestBox), new PropertyMetadata(null));

    /// <summary>The width the list is never narrower than; the box's own width is the other floor.</summary>
    public static readonly DependencyProperty ListMinWidthProperty = DependencyProperty.Register(
        nameof(ListMinWidth), typeof(double), typeof(SuggestBox), new PropertyMetadata(0d));

    public static readonly DependencyProperty MaxListHeightProperty = DependencyProperty.Register(
        nameof(MaxListHeight), typeof(double), typeof(SuggestBox), new PropertyMetadata(320d));

    /// <summary>What the list shows now. Read by the template; set only by this class.</summary>
    public static readonly DependencyProperty SuggestionsProperty = DependencyProperty.Register(
        nameof(Suggestions), typeof(IEnumerable), typeof(SuggestBox), new PropertyMetadata(null));

    /// <summary>True while the list is open. The chevron sets it; typing sets it through <see cref="Refresh"/>.</summary>
    public static readonly DependencyProperty IsListOpenProperty = DependencyProperty.Register(
        nameof(IsListOpen),
        typeof(bool),
        typeof(SuggestBox),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((SuggestBox)d).OnIsListOpenSet((bool)e.NewValue)));

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();

    private TextBox? _box;
    private ListBox? _list;

    /// <summary>The operator has changed the text since the last pick or since the cursor came here.</summary>
    private bool _typed;

    /// <summary>The text is being set by this class or a binding, not by the operator.</summary>
    private bool _quiet;

    /// <summary>The list is being opened by this class, which has already filled it.</summary>
    private bool _filling;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public string TextPath
    {
        get => (string)GetValue(TextPathProperty);
        set => SetValue(TextPathProperty, value);
    }

    public DataTemplate? ItemTemplate
    {
        get => (DataTemplate?)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public double ListMinWidth
    {
        get => (double)GetValue(ListMinWidthProperty);
        set => SetValue(ListMinWidthProperty, value);
    }

    public double MaxListHeight
    {
        get => (double)GetValue(MaxListHeightProperty);
        set => SetValue(MaxListHeightProperty, value);
    }

    public IEnumerable? Suggestions => (IEnumerable?)GetValue(SuggestionsProperty);

    public bool IsListOpen
    {
        get => (bool)GetValue(IsListOpenProperty);
        set => SetValue(IsListOpenProperty, value);
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

    public override void OnApplyTemplate()
    {
        if (_box is not null)
        {
            _box.TextChanged -= OnBoxTextChanged;
        }

        if (_list is not null)
        {
            _list.PreviewMouseLeftButtonUp -= OnListMouseUp;
        }

        base.OnApplyTemplate();
        _box = GetTemplateChild(BoxPart) as TextBox;
        _list = GetTemplateChild(ListPart) as ListBox;
        if (_box is not null)
        {
            SetBoxText(Text);
            _box.TextChanged += OnBoxTextChanged;
        }

        if (_list is not null)
        {
            _list.PreviewMouseLeftButtonUp += OnListMouseUp;
        }
    }

    /// <summary>Puts the cursor in the box with its text selected, ready to be typed over.</summary>
    public bool FocusBox()
    {
        ApplyTemplate();
        if (_box is null || !_box.Focus())
        {
            return false;
        }

        _box.SelectAll();
        return true;
    }

    /// <summary>
    /// Does now what leaving the box does: settles what was typed (an exact name becomes that entry,
    /// other text frees the selection) and writes the text to whatever <see cref="Text"/> is bound
    /// to. A shortcut that acts while the cursor is still in the box (Save, Enter to the next cell)
    /// calls this first; without the settling, a name typed over an item and saved at once kept the
    /// old item under the new name.
    /// </summary>
    public void CommitText()
    {
        Close();
        SettleTypedText();
        GetBindingExpression(TextProperty)?.UpdateSource();
    }

    /// <summary>
    /// Enter: picks the highlighted entry and closes the list. False when the list was not open or
    /// nothing in it was highlighted, so the caller does what Enter does anyway.
    /// </summary>
    public bool TryPick()
    {
        if (!IsListOpen)
        {
            return false;
        }

        var picked = _list?.SelectedItem;
        Close();
        if (picked is null)
        {
            return false;
        }

        Pick(picked);
        return true;
    }

    /// <summary>Moves the highlight of the open list one entry and stops at either end. Nothing is picked and the text is not touched.</summary>
    public void MoveHighlight(int delta)
    {
        if (!IsListOpen || _list is null)
        {
            return;
        }

        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, Math.Max(0, _list.Items.Count - 1));
        _list.ScrollIntoView(_list.SelectedItem);
    }

    public void Close()
    {
        SetCurrentValue(IsListOpenProperty, false);
        ClearValue(SuggestionsProperty);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        // Focus given to the control itself belongs in its box.
        if (ReferenceEquals(e.NewFocus, this))
        {
            FocusBox();
        }
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        if (e.NewValue is true)
        {
            _typed = false;
            return;
        }

        Close();
        SettleTypedText();
    }

    /// <summary>
    /// On leaving the box: what was typed may be exactly an entry, and then it is that entry. If it
    /// is not, it is free text and no longer the entry it was. Nothing typed, nothing to settle.
    /// </summary>
    internal void SettleTypedText()
    {
        if (!_typed)
        {
            return;
        }

        _typed = false;
        var text = (_box?.Text ?? Text).Trim();
        var exact = ItemsSource?.Cast<object?>()
            .FirstOrDefault(entry => entry is not null && string.Equals(TextOf(entry, TextPath), text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            if (!Equals(SelectedItem, exact))
            {
                Pick(exact);
            }
        }
        else if (SelectedItem is { } current && !string.Equals(TextOf(current, TextPath), text, StringComparison.OrdinalIgnoreCase))
        {
            SetCurrentValue(SelectedItemProperty, null);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            if (IsListOpen)
            {
                Close();
            }
            else
            {
                ShowAll();
            }

            e.Handled = true;
            return;
        }

        if (!IsListOpen)
        {
            return;
        }

        switch (key)
        {
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None:
                MoveHighlight(+1);
                e.Handled = true;
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None:
                MoveHighlight(-1);
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                e.Handled = TryPick();
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Opens, narrows or closes the list for what is in the box now.</summary>
    private void Refresh()
    {
        var matches = Filter(ItemsSource, TextPath, _box?.Text ?? Text);
        if (matches.Count == 0)
        {
            Close();
            return;
        }

        Show(matches, highlight: 0);
    }

    /// <summary>The chevron's list: every entry, with the selected one highlighted.</summary>
    private void ShowAll()
    {
        var all = ItemsSource?.Cast<object?>().Where(entry => entry is not null).Cast<object>().ToArray() ?? [];
        if (all.Length == 0)
        {
            Close();
            return;
        }

        FocusBox();
        Show(all, SelectedItem is { } selected ? Array.FindIndex(all, entry => Equals(entry, selected)) : -1);
    }

    private void Show(IReadOnlyList<object> entries, int highlight)
    {
        _filling = true;
        try
        {
            SetValue(SuggestionsProperty, entries);
            SetCurrentValue(IsListOpenProperty, true);
        }
        finally
        {
            _filling = false;
        }

        if (_list is not null)
        {
            _list.SelectedIndex = highlight;
            if (_list.SelectedItem is { } highlighted)
            {
                _list.ScrollIntoView(highlighted);
            }
        }
    }

    private void Pick(object picked)
    {
        _typed = false;
        SetCurrentValue(SelectedItemProperty, picked);
        // Setting the selection puts the entry's text in the box unless it is there already but for
        // its case; a pick always shows the entry as it is written.
        var text = TextOf(picked, TextPath);
        SetCurrentValue(TextProperty, text);
        SetBoxText(text);
        _box?.Select(_box.Text.Length, 0);
    }

    private void SetBoxText(string? text)
    {
        if (_box is null || _box.Text == (text ?? string.Empty))
        {
            return;
        }

        _quiet = true;
        try
        {
            _box.Text = text ?? string.Empty;
        }
        finally
        {
            _quiet = false;
        }
    }

    private void OnTextSet(string? text) => SetBoxText(text);

    private void OnSelectedItemSet(object? selected) => ShowSelection(selected);

    /// <summary>
    /// An entry set from outside shows its text, unless the box already holds that text in another
    /// case: a bill is reopened with the name as it was typed. A cleared selection leaves the text
    /// alone, because free text is a value.
    /// </summary>
    private void ShowSelection(object? selected)
    {
        // While the box is being built its properties arrive in no order this class can rely on:
        // in a row of the Invoice screen the selection comes before TextPath. Without the path only
        // plain text can be shown as it is. Anything else would be shown by its type's own text
        // ("ItemMasterEntry { Name = … }") and then written to whatever Text is bound to, so it
        // waits: OnTextPathSet shows it when the path arrives.
        if (selected is null || (TextPath.Length == 0 && selected is not string))
        {
            return;
        }

        var text = TextOf(selected, TextPath);
        if (!string.Equals(Text, text, StringComparison.OrdinalIgnoreCase))
        {
            SetCurrentValue(TextProperty, text);
        }
    }

    /// <summary>A selection that came before the path can be shown by its text now.</summary>
    private void OnTextPathSet() => ShowSelection(SelectedItem);

    private void OnIsListOpenSet(bool open)
    {
        if (!open)
        {
            ClearValue(SuggestionsProperty);
        }
        else if (!_filling)
        {
            ShowAll();
        }
    }

    private void OnBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_quiet || _box is null)
        {
            return;
        }

        // Every change the box did not get from this class is the operator's: a key, Backspace,
        // a paste or an undo.
        _typed = true;
        _quiet = true;
        try
        {
            SetCurrentValue(TextProperty, _box.Text);
        }
        finally
        {
            _quiet = false;
        }

        Refresh();
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_list is null
            || ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: { } picked })
        {
            return;
        }

        Close();
        Pick(picked);
        _box?.Focus();
        e.Handled = true;
    }

    /// <summary>So that a screen reader and UI Automation see the cell as one named control.</summary>
    private sealed class Peer(SuggestBox owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(SuggestBox);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;
    }
}
