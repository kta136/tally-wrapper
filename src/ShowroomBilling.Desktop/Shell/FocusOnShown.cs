using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Gives a control the keyboard focus each time it comes into view:
/// <c>shell:FocusOnShown.IsEnabled="True"</c>. Overlay dialogs stay loaded
/// and are only made visible, so "when shown" is the moment to focus their
/// first field. A text box also has its text selected, ready to be replaced.
/// </summary>
public static class FocusOnShown
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(FocusOnShown), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not UIElement target)
        {
            return;
        }

        target.IsVisibleChanged -= OnIsVisibleChanged;
        if (e.NewValue is true)
        {
            target.IsVisibleChanged += OnIsVisibleChanged;
        }
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not UIElement target)
        {
            return;
        }

        // Deferred: the control becomes visible before its dialog has been laid out,
        // and a control that has not been laid out cannot take the focus.
        target.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!target.IsVisible || !GetIsEnabled(target))
            {
                return;
            }

            target.Focus();
            Keyboard.Focus(target);
            if (target is TextBoxBase text)
            {
                text.SelectAll();
            }
            else if (target is PasswordBox password)
            {
                password.SelectAll();
            }
        });
    }
}
