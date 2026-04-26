using System.Windows.Input;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.ViewModels;

internal sealed class ShellDialogCoordinator(
    AdminTokenStore adminTokenStore,
    AdminUnlockViewModel admin,
    ChangeNumberDialogViewModel changeNumberDialog,
    ReasonPromptDialogViewModel reasonPromptDialog,
    Action<string?> setActiveDialog,
    Func<string?> getActiveDialog)
{
    private TaskCompletionSource<bool>? _adminDialogTcs;
    private TaskCompletionSource<(bool, string, string?)>? _changeNumberTcs;
    private TaskCompletionSource<string?>? _reasonPromptTcs;

    public Task RequestAdminUnlockAsync(CancellationToken cancellationToken)
    {
        if (adminTokenStore.IsUnlocked) return Task.CompletedTask;
        if (_adminDialogTcs is { Task.IsCompleted: false } inflight) return inflight.Task;

        _adminDialogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        setActiveDialog("admin");
        _ = admin.LoadStatusCommand.ExecuteAsync(null);
        return _adminDialogTcs.Task;
    }

    public void OnAdminTokenChanged(AdminUnlockResponse? unlock)
    {
        if (unlock is null || _adminDialogTcs is not { Task.IsCompleted: false } tcs)
        {
            return;
        }

        _adminDialogTcs = null;
        ReleasePointerState();
        setActiveDialog(null);
        tcs.TrySetResult(true);
    }

    public Task<(bool Confirmed, string NewNumber, string? Reason)> ShowChangeNumberDialogAsync(
        string? currentNumber,
        string? prefix,
        string? suffix,
        string fiscalYear,
        int padding)
    {
        if (_changeNumberTcs is { Task.IsCompleted: false })
        {
            return Task.FromResult((false, string.Empty, (string?)null));
        }

        var tcs = new TaskCompletionSource<(bool, string, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _changeNumberTcs = tcs;
        changeNumberDialog.Configure(currentNumber, prefix, suffix, fiscalYear, padding);
        setActiveDialog("changeNumber");
        return tcs.Task;
    }

    public void OnChangeNumberDialogClosed((bool Confirmed, string NewNumber, string? Reason) result)
    {
        var tcs = _changeNumberTcs;
        _changeNumberTcs = null;
        if (getActiveDialog() == "changeNumber") setActiveDialog(null);
        tcs?.TrySetResult(result);
    }

    public Task<string?> PromptReasonAsync(string title, string message, CancellationToken cancellationToken)
    {
        if (_reasonPromptTcs is { Task.IsCompleted: false }) return Task.FromResult<string?>(null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reasonPromptTcs = tcs;
        reasonPromptDialog.Configure(title, message);
        setActiveDialog("reasonPrompt");
        return tcs.Task;
    }

    public void OnReasonPromptDialogClosed(string? result)
    {
        var tcs = _reasonPromptTcs;
        _reasonPromptTcs = null;
        if (getActiveDialog() == "reasonPrompt") setActiveDialog(null);
        tcs?.TrySetResult(result);
    }

    public void OnActiveDialogChanged(string? value)
    {
        ReleasePointerState();

        if (value is null && _adminDialogTcs is { Task.IsCompleted: false } tcs)
        {
            _adminDialogTcs = null;
            tcs.TrySetResult(true);
        }
    }

    public static void ReleasePointerState()
    {
        try
        {
            Mouse.Captured?.ReleaseMouseCapture();
            Mouse.OverrideCursor = null;
        }
        catch
        {
            // Best effort. Dialog transitions should never leave pointer capture behind.
        }
    }
}
