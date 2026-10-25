using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShowroomBilling.Desktop.ViewModels.Handover;

/// <summary>How the operator closed the hand-over result dialog.</summary>
public enum HandoverResultChoice
{
    Close,
    OpenBills,
    TryAgain,
}

/// <summary>
/// The dialog a hand-over opens when something needs the operator: a row per bill with its result
/// and, under it, the reason; Try again when the server could not be reached, Open Bills and Close.
/// </summary>
public sealed partial class HandoverResultViewModel : ObservableObject
{
    public HandoverResultViewModel()
    {
        TryAgainCommand = new RelayCommand(() => Choose(HandoverResultChoice.TryAgain), () => CanTryAgain);
        OpenBillsCommand = new RelayCommand(() => Choose(HandoverResultChoice.OpenBills), () => CanOpenBills);
        CloseCommand = new RelayCommand(() => Choose(HandoverResultChoice.Close));
    }

    [ObservableProperty] private string title = string.Empty;

    /// <summary>The red line over the table: why nothing in the hand-over was used, or that the server cannot be reached.</summary>
    [ObservableProperty] private string? banner;

    [ObservableProperty] private bool canTryAgain;

    [ObservableProperty] private bool canOpenBills;

    public ObservableCollection<HandoverRowViewModel> Rows { get; } = [];

    public bool HasBanner => !string.IsNullOrWhiteSpace(Banner);

    public bool HasRows => Rows.Count > 0;

    /// <summary>Close takes the focus when there is nothing to try again, so a stray Enter only closes.</summary>
    public bool FocusesClose => !CanTryAgain;

    public IRelayCommand TryAgainCommand { get; }

    public IRelayCommand OpenBillsCommand { get; }

    public IRelayCommand CloseCommand { get; }

    /// <summary>Raised once per showing, with the button the operator chose; Esc counts as Close.</summary>
    public event EventHandler<HandoverResultChoice>? Closed;

    internal void Show(HandoverReport report)
    {
        Title = report.Title;
        Banner = report.Banner;
        CanTryAgain = report.CanTryAgain;
        CanOpenBills = report.CanOpenBills;
        Rows.Clear();
        foreach (var row in report.Rows)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>The shell closed the dialog (Esc, or another dialog took its place).</summary>
    internal void Dismiss() => Choose(HandoverResultChoice.Close);

    partial void OnBannerChanged(string? value) => OnPropertyChanged(nameof(HasBanner));

    partial void OnCanTryAgainChanged(bool value)
    {
        TryAgainCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(FocusesClose));
    }

    partial void OnCanOpenBillsChanged(bool value) => OpenBillsCommand.NotifyCanExecuteChanged();

    private void Choose(HandoverResultChoice choice) => Closed?.Invoke(this, choice);
}
