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

    private void OnBillRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
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
            await ExecuteSafelyAsync(
                () => ViewModel.PushSelectedCommand.ExecuteAsync(null),
                "Push selected");
        }
    }

    private async void OnRetrySelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.RetrySelectedCommand.CanExecute(null) == true)
        {
            await ExecuteSafelyAsync(
                () => ViewModel.RetrySelectedCommand.ExecuteAsync(null),
                "Retry selected");
        }
    }

    private async void OnPrintSelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ShellViewModel?.OpenSelectedBillsPrintPreviewCommand.CanExecute(null) == true)
        {
            await ExecuteSafelyAsync(
                () => ShellViewModel.OpenSelectedBillsPrintPreviewCommand.ExecuteAsync(null),
                "Print preview");
        }
    }

    private static BillListRowViewModel? RowFrom(object sender) =>
        sender is FrameworkElement { DataContext: BillListRowViewModel row } ? row : null;

    private async void OnPrintRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is null || row is null || ShellViewModel is null) return;
        // OnBillRowRightClick already called EnsureContextSelection(row), which keeps
        // an existing multi-selection intact when the right-clicked row is part of it
        // and otherwise narrows to just this row. Don't collapse it back here.
        if (ShellViewModel.OpenSelectedBillsPrintPreviewCommand.CanExecute(null))
        {
            await ExecuteSafelyAsync(
                () => ShellViewModel.OpenSelectedBillsPrintPreviewCommand.ExecuteAsync(null),
                "Print preview");
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
            await ExecuteSafelyAsync(
                () => ViewModel.EditRowCommand.ExecuteAsync(row),
                "Edit bill");
        }
    }

    private async void OnChangeNumberRowMenuItemClick(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (ViewModel is not null && row is not null && ViewModel.ChangeNumberRowCommand.CanExecute(row))
        {
            await ExecuteSafelyAsync(
                () => ViewModel.ChangeNumberRowCommand.ExecuteAsync(row),
                "Change bill number");
        }
    }

    private async void OnDeleteSelectedMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.DeleteSelectedCommand.CanExecute(null) == true)
        {
            await ExecuteSafelyAsync(
                () => ViewModel.DeleteSelectedCommand.ExecuteAsync(null),
                "Delete selected");
        }
    }

    private async Task ExecuteSafelyAsync(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            if (ViewModel is not null)
            {
                ViewModel.StatusMessage = $"{operation} failed: {ex.Message}";
            }
        }
    }
}
