using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.ViewModels.Admin;

namespace ShowroomBilling.Desktop.Views.Settings;

public partial class AdminSettingsSectionView : UserControl
{
    public AdminSettingsSectionView()
    {
        InitializeComponent();
    }

    private void AdminCurrentPasscodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box) vm.CurrentPasscode = box.Password;
    }

    private void AdminNewPasscodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box) vm.NewPasscode = box.Password;
    }

    private void AdminConfirmNewPasscodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box) vm.ConfirmNewPasscode = box.Password;
    }
}
