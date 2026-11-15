using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Views.Bills;

public partial class BillsView : UserControl, IHomeFocus
{
    public BillsView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        // The tab's keys (F5, Enter, Ctrl+R, …) are key bindings of this view, so they act only
        // while the keyboard is somewhere inside it. The rows of the list cannot take it, so the
        // view takes it itself: when the tab is shown, and when anything in it is clicked.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusHome));
            }
        };
        PreviewMouseDown += (_, _) =>
        {
            if (!IsKeyboardFocusWithin)
            {
                FocusHome();
            }
        };
    }

    public void FocusHome()
    {
        Focus();
        Keyboard.Focus(this);
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
