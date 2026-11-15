using System.Windows;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>What a combo box can be told beyond its own properties, read by the combo box template in <c>Controls.xaml</c>.</summary>
public static class ComboExtras
{
    /// <summary>False hides the arrow at the right of a combo box, for a list that is only being read.</summary>
    public static readonly DependencyProperty ShowChevronProperty = DependencyProperty.RegisterAttached(
        "ShowChevron", typeof(bool), typeof(ComboExtras), new PropertyMetadata(true));

    public static bool GetShowChevron(DependencyObject element) => (bool)element.GetValue(ShowChevronProperty);

    public static void SetShowChevron(DependencyObject element, bool value) => element.SetValue(ShowChevronProperty, value);
}
