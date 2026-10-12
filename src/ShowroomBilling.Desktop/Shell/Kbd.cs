using System.Windows;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// The keyboard-shortcut chip a button shows after its label:
/// <c>&lt;Button Content="Retry Push" shell:Kbd.Hint="Ctrl+R" /&gt;</c>.
/// The button template in <c>Styles.xaml</c> draws it. Outside a button, use
/// <c>&lt;ContentControl Style="{StaticResource Kbd}" Content="F2" /&gt;</c>.
/// </summary>
public static class Kbd
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(Kbd), new PropertyMetadata(null));

    public static string? GetHint(DependencyObject element) => (string?)element.GetValue(HintProperty);

    public static void SetHint(DependencyObject element, string? value) => element.SetValue(HintProperty, value);

    /// <summary>
    /// True on a filled button (<c>BtnPrimary</c>): its chip is translucent white, not the
    /// grey one a white button gets. The button styles set it; a view never does.
    /// </summary>
    public static readonly DependencyProperty OnFilledProperty = DependencyProperty.RegisterAttached(
        "OnFilled", typeof(bool), typeof(Kbd), new PropertyMetadata(false));

    public static bool GetOnFilled(DependencyObject element) => (bool)element.GetValue(OnFilledProperty);

    public static void SetOnFilled(DependencyObject element, bool value) => element.SetValue(OnFilledProperty, value);
}
