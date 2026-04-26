using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>
/// State for the shared "enter a reason" overlay (used by mark-posted /
/// mark-pending / void flows). Singleton on <c>MainWindowViewModel</c>;
/// <see cref="Configure"/> resets before open, <see cref="Closed"/> yields
/// the result (null = cancel).
/// </summary>
public partial class ReasonPromptDialogViewModel : ObservableObject
{
    private const int MinLength = 4;

    [ObservableProperty] private string titleText = string.Empty;
    [ObservableProperty] private string messageText = string.Empty;
    [ObservableProperty] private string reasonText = string.Empty;

    public ReasonPromptDialogViewModel()
    {
        OkCommand = new RelayCommand(OnOk, CanOk);
        CancelCommand = new RelayCommand(OnCancel);
    }

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>Fires on OK (with the reason) or Cancel (null).</summary>
    public event Action<string?>? Closed;

    public string HintText => $"Minimum {MinLength} characters.";

    public void Configure(string title, string message)
    {
        TitleText = title;
        MessageText = message;
        ReasonText = string.Empty;
    }

    partial void OnReasonTextChanged(string value) => OkCommand.NotifyCanExecuteChanged();

    private bool CanOk() => (ReasonText ?? string.Empty).Trim().Length >= MinLength;

    private void OnOk()
    {
        var v = (ReasonText ?? string.Empty).Trim();
        if (v.Length < MinLength) return;
        Closed?.Invoke(v);
    }

    private void OnCancel() => Closed?.Invoke(null);
}
