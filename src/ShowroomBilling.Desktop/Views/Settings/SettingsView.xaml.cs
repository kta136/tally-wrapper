using System;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Views.Settings;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnPreviewResizeThumbDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings) return;
        settings.Preview.PaneWidth = Math.Clamp(settings.Preview.PaneWidth - e.HorizontalChange, 320, Math.Max(320, ActualWidth - 280));
    }

    // PasswordBox can't bind two-way for security reasons. The Admin section's
    // DataContext is AdminVm (AdminUnlockViewModel), so these handlers route
    // typed chars into the VM properties the server-side command reads.
    private void AdminCurrentPasscodeBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box) vm.CurrentPasscode = box.Password;
    }

    private void AdminNewPasscodeBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box) vm.NewPasscode = box.Password;
    }

    private void AdminConfirmNewPasscodeBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: AdminUnlockViewModel vm } box) vm.ConfirmNewPasscode = box.Password;
    }
}
