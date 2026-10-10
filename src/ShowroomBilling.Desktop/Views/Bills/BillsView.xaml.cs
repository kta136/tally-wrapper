using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Views.Bills;

public partial class BillsView : UserControl
{
    public BillsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private BillsViewModel? ViewModel => DataContext as BillsViewModel;

    // While a dialog covers the list, keys still routed here (e.g. Ctrl+R,
    // which retries every selected bill without confirmation) must not act
    // on the list. Esc still reaches the window to close the dialog.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shell = Window.GetWindow(this)?.DataContext as MainWindowViewModel;
        if (shell?.IsAnyDialogOpen == true && e.Key != Key.Escape)
        {
            e.Handled = true;
        }
    }

    private void OnBillRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ViewModel?.OpenDetailsCommand?.CanExecute(null) == true)
        {
            ViewModel.OpenDetailsCommand.Execute(null);
        }
    }

    // A right-click acts on the row under the pointer: it becomes the
    // selection unless it is already part of a multi-row one.
    private void OnBillRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BillListRowViewModel row })
        {
            ViewModel?.EnsureContextSelection(row);
        }
    }
}
