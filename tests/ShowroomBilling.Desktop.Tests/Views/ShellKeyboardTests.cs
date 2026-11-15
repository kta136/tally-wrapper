using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The shell's keyboard: the two shortcuts that are a plain character, and where the cursor goes
/// when a dialog closes.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class ShellKeyboardTests
{
    [Fact]
    public void A_bare_key_shortcut_takes_the_key_only_when_it_ran_so_the_character_can_be_typed()
    {
        var allowed = true;
        var ran = new List<string>();
        var shortcuts = new RelayCommand(() => ran.Add("shortcuts"), () => allowed);
        var unlock = new RelayCommand(() => ran.Add("unlock"), () => allowed);

        Assert.True(BareKeyShortcuts.TryRun(Key.OemQuestion, ModifierKeys.Shift, shortcuts, unlock));
        Assert.True(BareKeyShortcuts.TryRun(Key.OemTilde, ModifierKeys.None, shortcuts, unlock));
        Assert.Equal(["shortcuts", "unlock"], ran);

        // A text box has the cursor, or a dialog is open: the key is left alone, and types.
        allowed = false;
        Assert.False(BareKeyShortcuts.TryRun(Key.OemQuestion, ModifierKeys.Shift, shortcuts, unlock));
        Assert.False(BareKeyShortcuts.TryRun(Key.OemTilde, ModifierKeys.None, shortcuts, unlock));

        // Other keys, and the same keys with other modifiers, are not these shortcuts.
        allowed = true;
        Assert.False(BareKeyShortcuts.TryRun(Key.OemQuestion, ModifierKeys.None, shortcuts, unlock));
        Assert.False(BareKeyShortcuts.TryRun(Key.OemTilde, ModifierKeys.Shift, shortcuts, unlock));
        Assert.False(BareKeyShortcuts.TryRun(Key.A, ModifierKeys.None, shortcuts, unlock));
        Assert.Equal(2, ran.Count);
    }

    [Fact]
    public void The_window_has_no_key_binding_for_a_bare_character()
    {
        // A key binding marks its key handled even while its command cannot run,
        // and a handled key types nothing: "?" and "`" could not be typed anywhere.
        var bare = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell();
            var window = new MainWindow(shell);
            return window.InputBindings
                .OfType<KeyBinding>()
                .Where(binding => binding.Key is Key.OemQuestion or Key.OemTilde)
                .Select(binding => binding.Key.ToString())
                .ToList();
        });

        Assert.Empty(bare);
    }

    [Fact]
    public void When_the_last_dialog_closes_the_cursor_goes_back_to_where_it_was_on_the_screen()
    {
        var seen = WpfTestHost.Run(() =>
        {
            // A window nobody sees: off every monitor, never activated, not in the taskbar.
            var onScreen = new TextBox();
            var alsoOnScreen = new TextBox();
            var home = new HomeView(new StackPanel { Children = { onScreen, alsoOnScreen } });
            var screen = new Grid { Children = { new ContentControl { Content = home } } };
            var topBarButton = new Button();
            var window = new Window
            {
                Width = 400,
                Height = 300,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new StackPanel { Children = { topBarButton, screen } },
            };
            var dialogOpen = false;
            var focus = new DialogFocusReturn(window, screen, () => dialogOpen);
            window.Show();
            try
            {
                // The cursor was in a field of the screen; then a top-bar button opened a dialog.
                focus.Remember(onScreen);
                focus.Remember(topBarButton);
                dialogOpen = true;
                FocusManager.SetFocusedElement(window, topBarButton);
                focus.RestoreNow();
                var whileOpen = FocusManager.GetFocusedElement(window);

                dialogOpen = false;
                focus.RestoreNow();
                var afterClose = FocusManager.GetFocusedElement(window);

                // The field has gone (the line of a bill that was saved): the tab's own place.
                ((StackPanel)home.Content).Children.Remove(onScreen);
                FocusManager.SetFocusedElement(window, topBarButton);
                focus.RestoreNow();

                return (
                    StayedWhileOpen: ReferenceEquals(whileOpen, topBarButton),
                    WentBack: ReferenceEquals(afterClose, onScreen),
                    HomeCalls: home.HomeCalls);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(seen.StayedWhileOpen);
        Assert.True(seen.WentBack);
        Assert.Equal(1, seen.HomeCalls);
    }

    private sealed class HomeView : UserControl, IHomeFocus
    {
        public HomeView(UIElement content) => Content = content;

        public int HomeCalls { get; private set; }

        public void FocusHome() => HomeCalls++;
    }
}
