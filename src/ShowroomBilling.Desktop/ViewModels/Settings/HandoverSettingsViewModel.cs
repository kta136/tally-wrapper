using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Hand-over, a section of this PC only: the hand-over key, shown masked, with Change…
/// (a small dialog to paste a new one) and Remove. Without a key this PC takes no hand-over.
/// </summary>
public sealed partial class HandoverSettingsViewModel : ObservableObject
{
    /// <summary>What the key field shows while a key is set: dots, never the key or a part of it.</summary>
    public const string MaskedKey = "••••••••••••••••••••••••";

    public HandoverSettingsViewModel(HandoverKeyStore store)
    {
        Store = store;
        Store.Changed += (_, _) => Refresh();
        Refresh();
    }

    public HandoverKeyStore Store { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplay))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool hasKey;

    /// <summary>What the operator pasted into the key dialog. Cleared whenever the dialog opens or closes.</summary>
    [ObservableProperty] private string newKey = string.Empty;

    /// <summary>Why the pasted text is not a key; empty while there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyError))]
    private string keyError = string.Empty;

    public bool HasKeyError => !string.IsNullOrEmpty(KeyError);

    /// <summary>The key field: dots while a key is set, "No key" while there is none.</summary>
    public string KeyDisplay => HasKey ? MaskedKey : "No key";

    /// <summary>Shows the key dialog; the shell wires it to its dialog host.</summary>
    public Action? ShowKeyDialog { get; set; }

    /// <summary>Hides the key dialog again.</summary>
    public Action? HideKeyDialog { get; set; }

    /// <summary>Asks before the key is removed; the shell wires it to the confirm overlay. With none wired the answer is "no".</summary>
    public Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; set; }

    partial void OnNewKeyChanged(string value)
    {
        if (HasKeyError)
        {
            KeyError = string.Empty;
        }
    }

    private void Refresh() => HasKey = Store.HasKey;

    /// <summary>Change…: opens the key dialog, empty.</summary>
    [RelayCommand]
    private void Change()
    {
        NewKey = string.Empty;
        KeyError = string.Empty;
        ShowKeyDialog?.Invoke();
    }

    [RelayCommand]
    private void SaveKey()
    {
        var problem = Store.Save(NewKey);
        if (problem is not null)
        {
            KeyError = problem;
            return;
        }

        CloseKeyDialog();
    }

    [RelayCommand]
    private void CancelKey() => CloseKeyDialog();

    private void CloseKeyDialog()
    {
        NewKey = string.Empty;
        KeyError = string.Empty;
        HideKeyDialog?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(HasKey))]
    private async Task RemoveAsync()
    {
        if (ConfirmHandler is null)
        {
            return;
        }

        var remove = await ConfirmHandler(new ConfirmRequest(
            "Remove the hand-over key?",
            "This PC stops taking hand-overs until a key is set again.",
            "Remove key",
            IsDestructive: true));
        if (remove)
        {
            Store.Remove();
        }
    }
}
