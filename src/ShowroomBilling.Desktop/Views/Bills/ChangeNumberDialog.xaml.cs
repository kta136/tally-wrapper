using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Views.Bills;

public partial class ChangeNumberDialog : UserControl
{
    public ChangeNumberDialog()
    {
        InitializeComponent();
        // Focus the NEW NUMBER field whenever the overlay becomes visible so
        // the operator can start typing immediately.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                NewNumberBox.Focus();
                NewNumberBox.SelectAll();
            }
        };
    }
}
