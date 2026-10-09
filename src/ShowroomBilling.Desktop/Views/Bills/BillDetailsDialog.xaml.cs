using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Views.Bills;

public partial class BillDetailsDialog : UserControl
{
    public BillDetailsDialog()
    {
        InitializeComponent();
        Focusable = true;
        FocusVisualStyle = null;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // The dialog's own Ctrl+S / Ctrl+R / Ctrl+P bindings only fire while
    // focus is inside it. Without this, focus stayed on the Bills list
    // underneath and those keys acted on the list's selection instead.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(this)));
        }
    }
}
