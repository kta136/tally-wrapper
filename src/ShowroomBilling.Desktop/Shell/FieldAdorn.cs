using System.Windows;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// What a text box can carry beside its text, drawn by the text box template in
/// <c>Styles.xaml</c>:
/// <c>&lt;TextBox shell:FieldAdorn.Placeholder="Search items" /&gt;</c> is the grey hint shown while
/// the box is empty, and <c>shell:FieldAdorn.Prefix="₹"</c> is a mark at its left edge (give the
/// box a Padding wide enough to clear it). The prefix may be an element, to colour it.
/// <c>shell:FieldAdorn.Suffix="seconds"</c> is the same at the right edge, for a unit.
/// </summary>
public static class FieldAdorn
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(FieldAdorn), new PropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject element) => (string?)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string? value) => element.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty PrefixProperty = DependencyProperty.RegisterAttached(
        "Prefix", typeof(object), typeof(FieldAdorn), new PropertyMetadata(null));

    public static object? GetPrefix(DependencyObject element) => element.GetValue(PrefixProperty);

    public static void SetPrefix(DependencyObject element, object? value) => element.SetValue(PrefixProperty, value);

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.RegisterAttached(
        "Suffix", typeof(object), typeof(FieldAdorn), new PropertyMetadata(null));

    public static object? GetSuffix(DependencyObject element) => element.GetValue(SuffixProperty);

    public static void SetSuffix(DependencyObject element, object? value) => element.SetValue(SuffixProperty, value);
}
