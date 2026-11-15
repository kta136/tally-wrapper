using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>A tab's view that knows where the cursor belongs when nothing else says: Party on Invoice, the list on Bills.</summary>
public interface IHomeFocus
{
    void FocusHome();
}

/// <summary>
/// Puts the cursor back on the screen when the last dialog closes. Dialogs are overlays in the
/// same window, so closing one hands the keyboard to nothing: the window had it, and neither a
/// typed letter nor a tab's own keys (F2, F3 and F4 on Invoice; F5, Enter and Ctrl+R on Bills)
/// reached anything until the operator clicked.
/// <para>It remembers the last control that had the cursor inside the screen region, which holds
/// the tabs' views: not a dialog's field and not a top-bar button. When no dialog is left the
/// cursor goes back there, or to the showing tab's <see cref="IHomeFocus"/> place when that
/// control has gone (the line of a bill that was just saved).</para>
/// </summary>
public sealed class DialogFocusReturn
{
    private readonly FrameworkElement _screen;
    private readonly Func<bool> _isAnyDialogOpen;
    private WeakReference<UIElement>? _last;

    public DialogFocusReturn(Window window, FrameworkElement screen, Func<bool> isAnyDialogOpen)
    {
        _screen = screen;
        _isAnyDialogOpen = isAnyDialogOpen;
        window.AddHandler(
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => Remember(e.NewFocus)),
            handledEventsToo: true);
    }

    /// <summary>Notes <paramref name="focused"/> as the place to come back to, if it is on the screen behind the dialogs.</summary>
    public void Remember(IInputElement? focused)
    {
        if (focused is UIElement element && IsOnScreen(element))
        {
            _last = new WeakReference<UIElement>(element);
        }
    }

    /// <summary>Call when a dialog has closed. Does nothing while another is still open.</summary>
    public void Restore()
        => _screen.Dispatcher.BeginInvoke(DispatcherPriority.Input, RestoreNow);

    /// <summary>What <see cref="Restore"/> does once the dialog is out of the way.</summary>
    public void RestoreNow()
    {
        if (_isAnyDialogOpen())
        {
            return;
        }

        // A view may have put the cursor somewhere itself by now.
        if (Keyboard.FocusedElement is UIElement current && IsOnScreen(current) && current.IsVisible)
        {
            return;
        }

        if (_last is not null && _last.TryGetTarget(out var last) && CanTakeFocus(last))
        {
            last.Focus();
            Keyboard.Focus(last);
            return;
        }

        ShowingHome()?.FocusHome();
    }

    private bool CanTakeFocus(UIElement element)
        => element.IsVisible && element.IsEnabled && element.Focusable && IsOnScreen(element);

    private bool IsOnScreen(DependencyObject element)
    {
        for (var current = element; current is not null; current = Parent(current))
        {
            if (ReferenceEquals(current, _screen))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject element)
        => element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);

    /// <summary>The view of the tab that is showing, if it names a home place.</summary>
    private IHomeFocus? ShowingHome()
    {
        if (_screen is not Panel panel)
        {
            return null;
        }

        foreach (var child in panel.Children)
        {
            if (child is ContentControl { IsVisible: true, Content: IHomeFocus home })
            {
                return home;
            }
        }

        return null;
    }
}
