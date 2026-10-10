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

    // While a dialog covers the list, keys still routed here (e.g. Ctrl+R,
    // which retries every selected bill without confirmation) must not act
    // on the list. Esc still reaches the window to close the dialog.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ShellViewModel?.ActiveDialog is not null && e.Key != Key.Escape)
        {
            e.Handled = true;
        }
    }

    private BillsViewModel? ViewModel => DataContext as BillsViewModel;
    private MainWindowViewModel? ShellViewModel => Window.GetWindow(this)?.DataContext as MainWindowViewModel;

    private void OnBillRowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenDetails();
        }
    }

    private void OnBillRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BillListRowViewModel row } && ViewModel is not null)
        {
            ViewModel.EnsureContextSelection(row);
        }
    }

    // Open Details and Print live on the shell view model, which the context
    // menu cannot reach by binding (its Tag carries the Bills view model), so
    // these two stay as click handlers.
    private void OnOpenDetailsMenuItemClick(object sender, RoutedEventArgs e) => OpenDetails();

    private void OpenDetails()
    {
        if (ShellViewModel?.OpenBillDetailsCommand.CanExecute(null) == true)
        {
            ShellViewModel.OpenBillDetailsCommand.Execute(null);
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
