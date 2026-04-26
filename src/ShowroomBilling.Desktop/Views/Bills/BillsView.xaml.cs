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
    }

    private BillsViewModel? ViewModel => DataContext as BillsViewModel;
    private MainWindowViewModel? ShellViewModel => Window.GetWindow(this)?.DataContext as MainWindowViewModel;

    private void OnBillRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BillListRowViewModel row } && ViewModel is not null)
        {
            ViewModel.SelectOnly(row);
        }
    }

    private void OnBillRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BillListRowViewModel row } && ViewModel is not null)
        {
            ViewModel.SelectOnly(row);
        }

        if (ShellViewModel?.OpenBillDetailsCommand.CanExecute(null) == true)
        {
            ShellViewModel.OpenBillDetailsCommand.Execute(null);
        }
    }

    private void OnBillRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnBillRowDoubleClick(sender, e);
        }
    }

    private void OnBillRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BillListRowViewModel row } && ViewModel is not null)
        {
            ViewModel.EnsureContextSelection(row);
        }
    }

    private void OnOpenDetailsMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ShellViewModel?.OpenBillDetailsCommand.CanExecute(null) == true)
        {
            ShellViewModel.OpenBillDetailsCommand.Execute(null);
        }
    }

    private async void OnPushSelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.PushSelectedCommand.CanExecute(null) == true)
        {
            await ViewModel.PushSelectedCommand.ExecuteAsync(null);
        }
    }

    private async void OnRetrySelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RetrySelectedCommand.CanExecute(null) == true)
        {
            await ViewModel.RetrySelectedCommand.ExecuteAsync(null);
        }
    }

    private async void OnPrintSelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ShellViewModel?.OpenSelectedBillsPrintPreviewCommand.CanExecute(null) == true)
        {
            await ShellViewModel.OpenSelectedBillsPrintPreviewCommand.ExecuteAsync(null);
        }
    }

    private static BillListRowViewModel? RowFrom(object sender) =>
        sender is FrameworkElement { DataContext: BillListRowViewModel row } ? row : null;

    private async void OnPushRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.PushRowCommand.CanExecute(row))
        {
            await ViewModel.PushRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnRetryRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.RetryRowCommand.CanExecute(row))
        {
            await ViewModel.RetryRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnRepostRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.RepostRowCommand.CanExecute(row))
        {
            await ViewModel.RepostRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnPrintRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is null || row is null || ShellViewModel is null) return;
        // OnBillRowRightClick already called EnsureContextSelection(row), which keeps
        // an existing multi-selection intact when the right-clicked row is part of it
        // and otherwise narrows to just this row. Don't collapse it back here.
        if (ShellViewModel.OpenSelectedBillsPrintPreviewCommand.CanExecute(null))
        {
            await ShellViewModel.OpenSelectedBillsPrintPreviewCommand.ExecuteAsync(null);
        }
    }

    private void OnCopyInvoiceNumberMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.CopyInvoiceNumberCommand.CanExecute(row))
        {
            ViewModel.CopyInvoiceNumberCommand.Execute(row);
        }
    }

    private async void OnEditRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.EditRowCommand.CanExecute(row))
        {
            await ViewModel.EditRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnChangeNumberRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.ChangeNumberRowCommand.CanExecute(row))
        {
            await ViewModel.ChangeNumberRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnMarkPostedRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.MarkPostedRowCommand.CanExecute(row))
        {
            await ViewModel.MarkPostedRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnMarkPendingRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.MarkPendingRowCommand.CanExecute(row))
        {
            await ViewModel.MarkPendingRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnDeleteRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.DeleteRowCommand.CanExecute(row))
        {
            await ViewModel.DeleteRowCommand.ExecuteAsync(row);
        }
    }

    private async void OnDeleteSelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.DeleteSelectedCommand.CanExecute(null) == true)
        {
            await ViewModel.DeleteSelectedCommand.ExecuteAsync(null);
        }
    }
}
