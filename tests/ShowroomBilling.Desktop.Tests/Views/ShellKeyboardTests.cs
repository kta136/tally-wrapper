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
        var batch = new RelayCommand(() => ran.Add("batch"), () => allowed);
        const ModifierKeys CtrlAlt = ModifierKeys.Control | ModifierKeys.Alt;

        Assert.True(BareKeyShortcuts.TryRun(Key.OemQuestion, ModifierKeys.Shift, shortcuts, unlock, batch));
        Assert.True(BareKeyShortcuts.TryRun(Key.OemTilde, ModifierKeys.None, shortcuts, unlock, batch));
        Assert.True(BareKeyShortcuts.TryRun(Key.G, CtrlAlt, shortcuts, unlock, batch));
        Assert.Equal(["shortcuts", "unlock", "batch"], ran);

        // A text box has the cursor, a dialog is open, or admin is locked: the key is left alone, and types.
        allowed = false;
        Assert.False(BareKeyShortcuts.TryRun(Key.OemQuestion, ModifierKeys.Shift, shortcuts, unlock, batch));
        Assert.False(BareKeyShortcuts.TryRun(Key.OemTilde, ModifierKeys.None, shortcuts, unlock, batch));
        Assert.False(BareKeyShortcuts.TryRun(Key.G, CtrlAlt, shortcuts, unlock, batch));

        // Other keys, and the same keys with other modifiers, are not these shortcuts.
        allowed = true;
        Assert.False(BareKeyShortcuts.TryRun(Key.OemQuestion, ModifierKeys.None, shortcuts, unlock, batch));
        Assert.False(BareKeyShortcuts.TryRun(Key.OemTilde, ModifierKeys.Shift, shortcuts, unlock, batch));
        Assert.False(BareKeyShortcuts.TryRun(Key.A, ModifierKeys.None, shortcuts, unlock, batch));
        Assert.False(BareKeyShortcuts.TryRun(Key.G, ModifierKeys.Control, shortcuts, unlock, batch));
        Assert.False(BareKeyShortcuts.TryRun(Key.G, ModifierKeys.None, shortcuts, unlock, batch));
        Assert.Equal(3, ran.Count);
    }

    [Fact]
    public void Ctrl_Alt_G_opens_the_batch_data_scheduler_only_while_admin_is_unlocked()
    {
        var seen = WpfTestHost.Run(() =>
        {
            var tokens = new ShowroomBilling.Desktop.Services.AdminTokenStore();
            var shell = DesignSample.CreateShell(tokenStore: tokens);
            // The sample leaves a confirm question open for its snapshot; answer it first.
            shell.ConfirmDialog.Cancel();

            // Locked: nothing happens and nothing hints that the key does anything.
            var whileLocked = shell.OpenSyntheticBatchKeyCommand.CanExecute(null);

            tokens.Set(new ShowroomBilling.Contracts.Admin.AdminUnlockResponse(
                "token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "admin/test"));
            var whileUnlocked = shell.OpenSyntheticBatchKeyCommand.CanExecute(null);
            shell.OpenSyntheticBatchKeyCommand.Execute(null);
            var opened = shell.ActiveDialog;

            // Another dialog is open (possibly an awaited prompt): the key never replaces it.
            shell.ActiveDialog = ShellDialogs.Health;
            var overADialog = shell.OpenSyntheticBatchKeyCommand.CanExecute(null);

            return (whileLocked, whileUnlocked, opened, overADialog);
        });

        Assert.False(seen.whileLocked);
        Assert.True(seen.whileUnlocked);
        Assert.Equal(ShellDialogs.SyntheticBatch, seen.opened);
        Assert.False(seen.overADialog);
    }

    [Fact]
    public void A_question_puts_the_keyboard_on_its_answer_and_a_warning_puts_it_on_Cancel()
    {
        // The question used to focus its button the moment it became visible, before it had been
        // laid out, and a button that has not been laid out cannot take the focus: the keyboard
        // stayed where it was, Enter did nothing, and a warning's Cancel was not under the keys.
        var seen = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell();
            shell.ConfirmDialog.Cancel();
            var elsewhere = new TextBox();
            var question = new ShowroomBilling.Desktop.Views.ConfirmDialog { Visibility = Visibility.Collapsed };
            var window = new Window
            {
                Left = -20000,
                Top = -20000,
                Width = 900,
                Height = 600,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                DataContext = shell,
                Content = new Grid { Children = { elsewhere, question } },
            };
            window.Show();
            try
            {
                string? Ask(ShowroomBilling.Desktop.ViewModels.ConfirmRequest request)
                {
                    question.Visibility = Visibility.Collapsed;
                    shell.ConfirmDialog.Cancel();
                    elsewhere.Focus();
                    WpfTestHost.DrainDispatcher();
                    _ = shell.ConfirmDialog.ShowAsync(request);
                    question.Visibility = Visibility.Visible;
                    WpfTestHost.DrainDispatcher();
                    return (FocusManager.GetFocusedElement(window) as Button)?.Name;
                }

                return (
                    Plain: Ask(new("Make the bills?", "They wait in the Bills tab.", "Make the bills")),
                    Warning: Ask(new("Make the bills?", "They wait in the Bills tab.", "Make the bills", IsWarning: true)),
                    Destructive: Ask(new("Delete the bill?", "It cannot be brought back.", "Delete", IsDestructive: true)));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("ConfirmButton", seen.Plain);
        Assert.Equal("CancelButton", seen.Warning);
        Assert.Equal("CancelButton", seen.Destructive);
    }

    [Fact]
    public void Open_Bills_tab_on_a_batch_result_closes_the_scheduler_and_shows_every_bill()
    {
        var seen = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell(tokenStore: DesignSample.UnlockedTokens());
            shell.ConfirmDialog.Cancel();
            // A filter left on from earlier would hide the pending bills a batch has just made.
            shell.Bills.StateFilter = shell.Bills.StateFilterOptions.Last();
            shell.Bills.SearchQuery = "nobody";
            shell.OpenSyntheticBatchKeyCommand.Execute(null);
            var opened = shell.ActiveDialog;

            shell.SyntheticBatch.OpenBillsCommand.Execute(null);

            return (opened, shell.ActiveDialog, shell.ActiveTab, shell.Bills.StateFilter, shell.Bills.SearchQuery);
        });

        Assert.Equal(ShellDialogs.SyntheticBatch, seen.opened);
        Assert.Null(seen.Item2);
        Assert.Equal(NavTab.Bills, seen.Item3);
        Assert.Equal("All", seen.Item4);
        Assert.Equal(string.Empty, seen.Item5);
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
