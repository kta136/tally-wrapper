using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Views.SyntheticBatch;

public partial class SyntheticBatchDialog : UserControl
{
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    public SyntheticBatchDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncInputsFromVm();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) SyncInputsFromVm();
        };
    }

    // The dialog inherits the shell view model; the batch view model hangs off it.
    private SyntheticBatchViewModel? Vm => (DataContext as MainWindowViewModel)?.SyntheticBatch;

    private void SyncInputsFromVm()
    {
        if (Vm is not { } vm) return;
        StartAtBox.Text = Format(vm.StartAt);
        EndAtBox.Text = Format(vm.EndAt);
    }

    private void OnStartAtLostFocus(object sender, RoutedEventArgs e)
        => CommitDateTime(sender, vm => vm.StartAt, (vm, value) => vm.StartAt = value);

    private void OnEndAtLostFocus(object sender, RoutedEventArgs e)
        => CommitDateTime(sender, vm => vm.EndAt, (vm, value) => vm.EndAt = value);

    /// <summary>Takes what was typed if it is a date and time; otherwise puts the current value back.</summary>
    private void CommitDateTime(
        object sender,
        Func<SyntheticBatchViewModel, DateTimeOffset> current,
        Action<SyntheticBatchViewModel, DateTimeOffset> assign)
    {
        if (Vm is not { } vm || sender is not TextBox box) return;
        if (TryParseLocal(box.Text, out var parsed))
        {
            assign(vm, parsed);
        }
        else
        {
            box.Text = Format(current(vm));
        }
    }

    private static string Format(DateTimeOffset value)
        => value.LocalDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a local date and time. The offset is the one in force on that
    /// date, not today's, so the value is right either side of a clock change.
    /// </summary>
    internal static bool TryParseLocal(string? text, out DateTimeOffset value)
    {
        if (DateTime.TryParseExact(text, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            || DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out local))
        {
            value = new DateTimeOffset(local);
            return true;
        }

        value = default;
        return false;
    }
}
