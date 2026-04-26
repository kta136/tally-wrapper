using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Views.Bills;

public partial class ReasonPromptDialog : UserControl
{
    public ReasonPromptDialog()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ReasonBox.Focus();
                ReasonBox.SelectAll();
            }
        };
    }
}
