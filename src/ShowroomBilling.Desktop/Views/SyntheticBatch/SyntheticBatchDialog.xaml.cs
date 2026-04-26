using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Views.SyntheticBatch;

public partial class SyntheticBatchDialog : UserControl
{
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    public SyntheticBatchDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SyncInputsFromVm();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) SyncInputsFromVm();
    }

    private SyntheticBatchViewModel? Vm => FindVm();

    private SyntheticBatchViewModel? FindVm()
    {
        // DataContext is the wrapper (.SyntheticBatch); resolve it.
        var ctx = DataContext;
        if (ctx is null) return null;
        var prop = ctx.GetType().GetProperty("SyntheticBatch");
        return prop?.GetValue(ctx) as SyntheticBatchViewModel;
    }

    private void SyncInputsFromVm()
    {
        var vm = Vm;
        if (vm is null) return;
        StartAtBox.Text = vm.StartAt.LocalDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
        EndAtBox.Text = vm.EndAt.LocalDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
    }

    private void OnStartAtLostFocus(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not TextBox box) return;
        if (TryParse(box.Text, out var parsed))
        {
            vm.StartAt = parsed;
        }
        else
        {
            box.Text = vm.StartAt.LocalDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
        }
    }

    private void OnEndAtLostFocus(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not TextBox box) return;
        if (TryParse(box.Text, out var parsed))
        {
            vm.EndAt = parsed;
        }
        else
        {
            box.Text = vm.EndAt.LocalDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
        }
    }

    private static bool TryParse(string? text, out DateTimeOffset value)
    {
        if (DateTime.TryParseExact(text, DateTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var dt))
        {
            value = new DateTimeOffset(dt, DateTimeOffset.Now.Offset);
            return true;
        }
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt2))
        {
            value = new DateTimeOffset(dt2, DateTimeOffset.Now.Offset);
            return true;
        }
        value = default;
        return false;
    }
}
