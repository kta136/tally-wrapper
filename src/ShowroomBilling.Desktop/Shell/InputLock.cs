using System.Windows;
using System.Windows.Input;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Keeps the keyboard and the mouse away from an element, and everything in
/// it, while something is in flight: <c>shell:InputLock.IsLocked="{Binding IsSaving}"</c>.
/// The element looks the same and keeps the focus; keys and clicks simply do
/// not arrive. The Invoice tab uses it while a bill is being saved or loaded:
/// what was typed in that moment was not in the saved bill, yet it showed on
/// the form as if it were, until the form was emptied.
/// </summary>
public static class InputLock
{
    public static readonly DependencyProperty IsLockedProperty = DependencyProperty.RegisterAttached(
        "IsLocked", typeof(bool), typeof(InputLock), new PropertyMetadata(false, OnIsLockedChanged));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(InputLock), new PropertyMetadata(false));

    public static bool GetIsLocked(DependencyObject element) => (bool)element.GetValue(IsLockedProperty);

    public static void SetIsLocked(DependencyObject element, bool value) => element.SetValue(IsLockedProperty, value);

    private static void OnIsLockedChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target || e.NewValue is not true || (bool)target.GetValue(IsHookedProperty))
        {
            return;
        }

        // Hooked once, on the way down to the control the input is meant for,
        // so that nothing inside the element sees it. The handlers stay; they
        // do nothing while the element is not locked.
        target.SetValue(IsHookedProperty, true);
        target.PreviewKeyDown += Swallow;
        target.PreviewKeyUp += Swallow;
        target.PreviewTextInput += Swallow;
        target.PreviewMouseDown += Swallow;
        target.PreviewMouseUp += Swallow;
        target.PreviewMouseWheel += Swallow;
    }

    private static void Swallow(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element || !GetIsLocked(element))
        {
            return;
        }

        // Alt combinations belong to Windows (Alt+F4, Alt+Tab, the window menu).
        if (e is KeyEventArgs { Key: Key.System })
        {
            return;
        }

        e.Handled = true;
    }
}
