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

            // A warning starts on Cancel, so Enter or Space does not go ahead by accident.
            var isWarning = (DataContext as MainWindowViewModel)?.ConfirmDialog.IsWarning == true;
            (isWarning ? CancelButton : ConfirmButton).Focus();
        };
    }
}
