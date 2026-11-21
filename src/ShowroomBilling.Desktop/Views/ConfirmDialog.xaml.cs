using System.Windows.Controls;
using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Views;

public partial class ConfirmDialog : UserControl
{
    public ConfirmDialog()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true)
            {
                return;
            }

            // A warning starts on Cancel, so Enter or Space does not go ahead by accident. Deferred,
            // as FocusOnShown is: the dialog becomes visible before it has been laid out, and a
            // button that has not been laid out cannot take the focus. Focused at once, as this was,
            // neither button got it: the keyboard stayed on the window, Enter did nothing, and a
            // warning's Cancel was never the button under the keys.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                if (!IsVisible || DataContext is not MainWindowViewModel shell)
                {
                    return;
                }

                var confirm = shell.ConfirmDialog;
                var button = confirm.IsWarning ? CancelButton : confirm.IsDestructive ? DestroyButton : ConfirmButton;
                button.Focus();
                System.Windows.Input.Keyboard.Focus(button);
            });
        };
    }
}
