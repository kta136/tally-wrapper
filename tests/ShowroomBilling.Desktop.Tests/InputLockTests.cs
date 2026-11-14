using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.Views;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The lock the Invoice tab puts on its form while a bill is being saved:
/// nothing typed or clicked in that moment may reach the fields.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class InputLockTests
{
    [Fact]
    public void While_locked_keys_text_and_clicks_do_not_reach_what_is_inside()
    {
        var result = WpfTestHost.Run(() =>
        {
            var box = new TextBox();
            var panel = new Grid();
            panel.Children.Add(box);
            var window = new Window { Content = panel };
            var handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var source = System.Windows.Interop.HwndSource.FromHwnd(handle);

            bool Press(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                box.RaiseEvent(args);
                return args.Handled;
            }

            bool Type()
            {
                var args = new TextCompositionEventArgs(
                    Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, box, "7"))
                {
                    RoutedEvent = UIElement.PreviewTextInputEvent,
                };
                box.RaiseEvent(args);
                return args.Handled;
            }

            bool Click()
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                };
                box.RaiseEvent(args);
                return args.Handled;
            }

            var before = (Key: Press(Key.A), Text: Type(), Click: Click());
            InputLock.SetIsLocked(panel, true);
            var locked = (Key: Press(Key.A), Text: Type(), Click: Click(), Alt: Press(Key.System));
            InputLock.SetIsLocked(panel, false);
            var after = (Key: Press(Key.A), Text: Type(), Click: Click());
            window.Content = null;
            return (before, locked, after);
        });

        Assert.Equal((false, false, false), result.before);
        Assert.True(result.locked.Key);
        Assert.True(result.locked.Text);
        Assert.True(result.locked.Click);
        // Alt combinations are Windows' own: Alt+F4 must still close the app.
        Assert.False(result.locked.Alt);
        Assert.Equal((false, false, false), result.after);
    }
}
