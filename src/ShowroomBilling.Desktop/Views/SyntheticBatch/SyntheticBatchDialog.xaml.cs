using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Views.SyntheticBatch;

public partial class SyntheticBatchDialog : UserControl
{
    private const string DateTimeFormat = "yyyy-MM-dd HH:mm";

    private SyntheticBatchViewModel? _watched;

    // What each time box was last given. A box still holding it has not been typed in, so it
    // follows the view model, and leaving it commits nothing.
    private string? _startShown;
    private string? _endShown;

    public SyntheticBatchDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Watch(Vm);
            SyncInputsFromVm();
        };
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
        StartAtBox.Text = _startShown = Format(vm.StartAt);
        EndAtBox.Text = _endShown = Format(vm.EndAt);
    }

    // The window is set again once the last bill's time has been read, after the dialog is shown.
    private void Watch(SyntheticBatchViewModel? vm)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
        _watched = vm;
        if (vm is not null) vm.PropertyChanged += OnVmPropertyChanged;
    }

    // A box the operator has typed in is left alone; its own LostFocus commits it.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SyntheticBatchViewModel vm) return;
        if (e.PropertyName == nameof(SyntheticBatchViewModel.StartAt) && StartAtBox.Text == _startShown)
            StartAtBox.Text = _startShown = Format(vm.StartAt);
        else if (e.PropertyName == nameof(SyntheticBatchViewModel.EndAt) && EndAtBox.Text == _endShown)
            EndAtBox.Text = _endShown = Format(vm.EndAt);
    }

    private void OnStartAtLostFocus(object sender, RoutedEventArgs e)
        => _startShown = CommitDateTime(StartAtBox, _startShown, vm => vm.StartAt, (vm, value) => vm.StartAt = value);

    private void OnEndAtLostFocus(object sender, RoutedEventArgs e)
        => _endShown = CommitDateTime(EndAtBox, _endShown, vm => vm.EndAt, (vm, value) => vm.EndAt = value);

    /// <summary>
    /// Takes what was typed if it is a date and time; otherwise puts the current value back.
    /// Returns what the box now shows. An untouched box commits nothing, so it never writes
    /// back a time the view model has moved on from.
    /// </summary>
    private string? CommitDateTime(
        TextBox box,
        string? shown,
        Func<SyntheticBatchViewModel, DateTimeOffset> current,
        Action<SyntheticBatchViewModel, DateTimeOffset> assign)
    {
        if (Vm is not { } vm || box.Text == shown) return shown;
        if (TryParseLocal(box.Text, out var parsed))
        {
            assign(vm, parsed);
        }

        box.Text = Format(current(vm));
        return box.Text;
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
