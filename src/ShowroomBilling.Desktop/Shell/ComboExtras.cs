using System.Windows;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// What a combo box's drop-down can carry beyond its items, drawn by the combo box template in
/// <c>Controls.xaml</c>: a minimum width, so a narrow table cell can open a wide list, and a footer
/// under the items, such as the keys that work in the list.
/// </summary>
public static class ComboExtras
{
    public static readonly DependencyProperty DropDownMinWidthProperty = DependencyProperty.RegisterAttached(
        "DropDownMinWidth", typeof(double), typeof(ComboExtras), new PropertyMetadata(0d));

    public static double GetDropDownMinWidth(DependencyObject element) => (double)element.GetValue(DropDownMinWidthProperty);

    public static void SetDropDownMinWidth(DependencyObject element, double value) => element.SetValue(DropDownMinWidthProperty, value);

    /// <summary>False hides the arrow at the right of a combo box, for a list that is only being read.</summary>
    public static readonly DependencyProperty ShowChevronProperty = DependencyProperty.RegisterAttached(
        "ShowChevron", typeof(bool), typeof(ComboExtras), new PropertyMetadata(true));

    public static bool GetShowChevron(DependencyObject element) => (bool)element.GetValue(ShowChevronProperty);

    public static void SetShowChevron(DependencyObject element, bool value) => element.SetValue(ShowChevronProperty, value);

    public static readonly DependencyProperty DropDownFooterProperty = DependencyProperty.RegisterAttached(
        "DropDownFooter", typeof(object), typeof(ComboExtras), new PropertyMetadata(null));

    public static object? GetDropDownFooter(DependencyObject element) => element.GetValue(DropDownFooterProperty);

    public static void SetDropDownFooter(DependencyObject element, object? value) => element.SetValue(DropDownFooterProperty, value);
}
