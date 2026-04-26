using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.Views.Admin;

public partial class AdminUnlockDialog : UserControl
{
    public AdminUnlockDialog()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void UnlockPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box)
        {
            vm.Passcode = box.Password;
        }
    }

    private void UnlockPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } && vm.UnlockCommand.CanExecute(null))
        {
            vm.UnlockCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void InitialNewPasscodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box)
        {
            vm.NewPasscode = box.Password;
        }
    }

    private void InitialConfirmPasscodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box)
        {
            vm.ConfirmNewPasscode = box.Password;
        }
    }

    private void InitialConfirmPasscodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } && vm.SetOrChangePasscodeCommand.CanExecute(null))
        {
            vm.SetOrChangePasscodeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
        {
            return;
        }

        // Defer to ensure the dialog's layout pass has completed before focusing.
        // Focus the form that's actually showing — initial-setup form takes priority
        // over the unlock form when no passcode is configured yet.
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            var target = InitialNewPasscodeBox.IsVisible
                ? (Control)InitialNewPasscodeBox
                : UnlockPasswordBox;
            target.Focus();
            Keyboard.Focus(target);
        }), DispatcherPriority.Input);
    }
}
