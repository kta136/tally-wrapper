using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>What the "enter a reason" overlay asks and says.</summary>
/// <param name="Title">The action, in sentence case: "Mark as pushed".</param>
/// <param name="Context">The line under the title: which bill or bills it is about.</param>
/// <param name="Warning">What the action does, for the banner. <c>**Bold**</c> marks the words to read twice.</param>
/// <param name="ConfirmText">The label of the confirm button; it names the action.</param>
public sealed record ReasonRequest(string Title, string Context, string Warning, string ConfirmText);

/// <summary>
/// State for the shared "enter a reason" overlay (used by mark-posted /
/// mark-pending flows). Singleton on <c>MainWindowViewModel</c>;
/// <see cref="Configure"/> resets before open, <see cref="Closed"/> yields
/// the result (null = cancel).
/// </summary>
public partial class ReasonPromptDialogViewModel : ObservableObject
{
    private const int MinLength = 4;

    [ObservableProperty] private string titleText = string.Empty;
    [ObservableProperty] private string contextText = string.Empty;
    [ObservableProperty] private string warningText = string.Empty;
    [ObservableProperty] private string confirmText = "OK";
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

    public string HintText => $"At least {MinLength} characters.";

    /// <summary>How much has been typed. Green once it is enough.</summary>
    public string CountText => $"{TrimmedLength} so far";

    public bool IsLongEnough => TrimmedLength >= MinLength;

    private int TrimmedLength => (ReasonText ?? string.Empty).Trim().Length;

    public void Configure(ReasonRequest request)
    {
        TitleText = request.Title;
        ContextText = request.Context;
        WarningText = request.Warning;
        ConfirmText = request.ConfirmText;
        ReasonText = string.Empty;
    }

    partial void OnReasonTextChanged(string value)
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(IsLongEnough));
        OkCommand.NotifyCanExecuteChanged();
    }

    private bool CanOk() => IsLongEnough;

    private void OnOk()
    {
        if (!IsLongEnough) return;
        Closed?.Invoke((ReasonText ?? string.Empty).Trim());
    }

    private void OnCancel() => Closed?.Invoke(null);
}
