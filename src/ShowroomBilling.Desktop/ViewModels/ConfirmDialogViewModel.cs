using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShowroomBilling.Desktop.ViewModels;

/// <summary>
/// A yes/no question for the operator. <see cref="IsWarning"/> marks an action
/// that is hard to undo: the dialog then shows the warning banner and puts
/// keyboard focus on Cancel, so a stray Enter does not go ahead.
/// </summary>
public sealed record ConfirmRequest(
    string Title,
    string Message,
    string ConfirmText = "OK",
    bool IsWarning = false);

/// <summary>
/// The shared confirmation overlay. It sits above every other dialog, so a
/// question asked from inside one (repost from Bill Details) does not replace
/// it. View models reach it through a <c>ConfirmHandler</c> delegate, which
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

    public ConfirmDialogViewModel()
    {
        ConfirmCommand = new RelayCommand(() => Close(true));
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

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
        IsWarning = request.IsWarning;
        IsOpen = true;
        return _pending.Task;
    }

    /// <summary>Esc: the same as pressing Cancel.</summary>
    public void Cancel() => Close(false);

    private void Close(bool confirmed)
    {
        IsOpen = false;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(confirmed);
    }
}
