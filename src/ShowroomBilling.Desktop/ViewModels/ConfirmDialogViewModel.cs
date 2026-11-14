using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShowroomBilling.Desktop.ViewModels;

/// <summary>
/// A yes/no question for the operator. <see cref="IsWarning"/> marks an action
/// that is hard to undo: the dialog then puts keyboard focus on Cancel, so a stray
/// Enter does not go ahead. <see cref="Note"/> is the one sentence to read twice, shown
/// in a banner under the question; <see cref="IsDestructive"/> turns the confirm
/// button red, for an action that deletes something.
/// </summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmText = "OK",
    bool IsWarning = false,
    string? Note = null,
    bool IsDestructive = false);

/// <summary>
/// The shared confirmation overlay. It sits above every other dialog, so a
/// question asked from inside one (repost from Bill Details) does not replace
/// it. View models reach it through <c>IDialogService.ConfirmAsync</c>, which
/// keeps them testable without a message box appearing.
/// </summary>
public partial class ConfirmDialogViewModel : ObservableObject
{
    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string titleText = string.Empty;
    [ObservableProperty] private string messageText = string.Empty;
    [ObservableProperty] private string confirmText = "OK";
    [ObservableProperty] private bool isWarning;
    [ObservableProperty] private bool isDestructive;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string noteText = string.Empty;

    public bool HasNote => !string.IsNullOrWhiteSpace(NoteText);

    /// <summary>
    /// Asks the question and completes with the answer. A question asked while
    /// another is still open is answered "no": the operator never saw it.
    /// </summary>
    public Task<bool> ShowAsync(ConfirmRequest request)
    {
        if (_pending is { Task.IsCompleted: false })
        {
            return Task.FromResult(false);
        }

        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TitleText = request.Title;
        MessageText = request.Message;
        ConfirmText = request.ConfirmText;
        IsWarning = request.IsWarning || request.IsDestructive;
        IsDestructive = request.IsDestructive;
        NoteText = request.Note ?? string.Empty;
        IsOpen = true;
        return _pending.Task;
    }

    [RelayCommand]
    private void Confirm() => Close(true);

    /// <summary>The Cancel button, and Esc.</summary>
    [RelayCommand]
    public void Cancel() => Close(false);

    private void Close(bool confirmed)
    {
        IsOpen = false;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(confirmed);
    }
}
