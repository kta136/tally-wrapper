using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Views.SyntheticBatch;

/// <summary>
/// The Batch Data Scheduler. Everything it shows and reads is bound: the two times are a date
/// picker and a typed time of day each, held by the view model (<c>FromDate</c> / <c>FromTime</c>).
/// </summary>
public partial class SyntheticBatchDialog : UserControl
{
    public SyntheticBatchDialog()
    {
        InitializeComponent();
    }
}
