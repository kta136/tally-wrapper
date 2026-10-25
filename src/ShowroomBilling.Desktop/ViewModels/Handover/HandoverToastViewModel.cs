using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShowroomBilling.Desktop.ViewModels.Handover;

/// <summary>
/// The card at the bottom right of the screen after a hand-over that went well: the title, a row
/// per bill (number, party, total, and its payment or what happened to it) and Open Bills. It does
/// not take the focus and fades by itself; a click on it opens Bills.
/// </summary>
public sealed partial class HandoverToastViewModel : ObservableObject
{
    public HandoverToastViewModel()
    {
        OpenBillsCommand = new RelayCommand(() => OpenBillsRequested?.Invoke(this, EventArgs.Empty));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    [ObservableProperty] private string title = string.Empty;

    /// <summary>"Pending in Bills" when the hand-over saved something; empty when every bill was unchanged.</summary>
    [ObservableProperty] private string footerNote = string.Empty;

    public ObservableCollection<HandoverRowViewModel> Rows { get; } = [];

    public IRelayCommand OpenBillsCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public event EventHandler? OpenBillsRequested;

    public event EventHandler? CloseRequested;

    internal void Show(HandoverReport report)
    {
        Title = report.Title;
        FooterNote = report.FooterNote;
        Rows.Clear();
        foreach (var row in report.Rows)
        {
            Rows.Add(row);
        }
    }
}
