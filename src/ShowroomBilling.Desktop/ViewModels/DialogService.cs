using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.ViewModels;

/// <summary>What the Change bill number dialog shows: the bill's number now, and what a new one is formatted with.</summary>
public sealed record ChangeNumberPrompt(
    string? CurrentNumber,
    string? Prefix,
    string? Suffix,
    string FiscalYear,
    int Padding,
    string? Context);

/// <summary>
/// The shell's dialogs, as a view model may use them: ask a question, ask for
/// the admin passcode, ask for a reason or a new number, show or close one of
/// the shell's named dialogs. A view model gets it through its constructor, so
/// none of this depends on the shell having wired a handler.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Asks a yes/no question in the confirm overlay, which sits above every
    /// other dialog. Esc, and a question asked while another is open, is "no".
    /// </summary>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>
    /// Opens the admin unlock unless admin is already unlocked, and completes
    /// when it closes, unlocked or not: the caller reads the token afterwards.
    /// </summary>
    Task RequestAdminUnlockAsync(CancellationToken cancellationToken);

    /// <summary>Asks for a reason; null when the operator cancelled.</summary>
    Task<string?> PromptReasonAsync(ReasonRequest request, CancellationToken cancellationToken);

    /// <summary>Asks for a new bill number; not confirmed when the operator cancelled.</summary>
    Task<(bool Confirmed, string NewNumber, string? Reason)> PromptChangeNumberAsync(ChangeNumberPrompt prompt);

    /// <summary>Shows one of the <see cref="ShellDialogs"/>.</summary>
    void Show(string dialog);

    /// <summary>Closes <paramref name="dialog"/> if it is the one showing.</summary>
    void Close(string dialog);
}

/// <summary>
/// Owns which dialog is open (<see cref="ActiveDialog"/>, which the main
/// window binds through the shell) and the prompts a caller awaits. A prompt
/// that another dialog replaces can never be answered, so it is resolved as
/// cancelled the moment that happens; otherwise its caller would wait for
/// ever and later prompts would be refused as "already open".
/// </summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    private readonly AdminTokenStore _adminTokens;
    private readonly AdminUnlockViewModel _admin;
    private TaskCompletionSource<bool>? _adminUnlock;
    private TaskCompletionSource<(bool, string, string?)>? _changeNumber;
    private TaskCompletionSource<string?>? _reason;

    public DialogService(AdminTokenStore adminTokens, AdminUnlockViewModel admin)
    {
        _adminTokens = adminTokens;
        _admin = admin;
        _adminTokens.Changed += OnAdminTokenChanged;
        ChangeNumberDialog.Closed += OnChangeNumberDialogClosed;
        ReasonPromptDialog.Closed += OnReasonPromptDialogClosed;
    }

    public ChangeNumberDialogViewModel ChangeNumberDialog { get; } = new();

    public ReasonPromptDialogViewModel ReasonPromptDialog { get; } = new();

    public ConfirmDialogViewModel ConfirmDialog { get; } = new();

    /// <summary>One of the <see cref="ShellDialogs"/> names, or null when none is open.</summary>
    [ObservableProperty]
    private string? activeDialog;

    public Task<bool> ConfirmAsync(ConfirmRequest request) => ConfirmDialog.ShowAsync(request);

    public Task RequestAdminUnlockAsync(CancellationToken cancellationToken)
    {
        if (_adminTokens.IsUnlocked) return Task.CompletedTask;
        if (_adminUnlock is { Task.IsCompleted: false } inFlight) return inFlight.Task;

        _adminUnlock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ActiveDialog = ShellDialogs.Admin;
        _ = _admin.LoadStatusCommand.ExecuteAsync(null);
        return _adminUnlock.Task;
    }

    public Task<(bool Confirmed, string NewNumber, string? Reason)> PromptChangeNumberAsync(ChangeNumberPrompt prompt)
    {
        if (_changeNumber is { Task.IsCompleted: false })
        {
            return Task.FromResult((false, string.Empty, (string?)null));
        }

        var pending = new TaskCompletionSource<(bool, string, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _changeNumber = pending;
        ChangeNumberDialog.Configure(
            prompt.CurrentNumber, prompt.Prefix, prompt.Suffix, prompt.FiscalYear, prompt.Padding, prompt.Context);
        ActiveDialog = ShellDialogs.ChangeNumber;
        return pending.Task;
    }

    public Task<string?> PromptReasonAsync(ReasonRequest request, CancellationToken cancellationToken)
    {
        if (_reason is { Task.IsCompleted: false }) return Task.FromResult<string?>(null);

        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reason = pending;
        ReasonPromptDialog.Configure(request);
        ActiveDialog = ShellDialogs.ReasonPrompt;
        return pending.Task;
    }

    public void Show(string dialog) => ActiveDialog = dialog;

    public void Close(string dialog)
    {
        if (ActiveDialog == dialog) ActiveDialog = null;
    }

    /// <summary>
    /// Esc on a prompt that is being awaited: answers it "cancelled" and closes it.
    /// False when the open dialog is not such a prompt.
    /// </summary>
    public bool CancelPrompt()
    {
        if (ActiveDialog == ShellDialogs.ChangeNumber)
        {
            OnChangeNumberDialogClosed((false, string.Empty, null));
            return true;
        }

        if (ActiveDialog == ShellDialogs.ReasonPrompt)
        {
            OnReasonPromptDialogClosed(null);
            return true;
        }

        return false;
    }

    partial void OnActiveDialogChanged(string? value)
    {
        ReleasePointerState();

        if (value is null && _adminUnlock is { Task.IsCompleted: false } unlock)
        {
            _adminUnlock = null;
            unlock.TrySetResult(true);
        }

        if (value != ShellDialogs.ChangeNumber && _changeNumber is { Task.IsCompleted: false } changeNumber)
        {
            _changeNumber = null;
            changeNumber.TrySetResult((false, string.Empty, null));
        }

        if (value != ShellDialogs.ReasonPrompt && _reason is { Task.IsCompleted: false } reason)
        {
            _reason = null;
            reason.TrySetResult(null);
        }
    }

    /// <summary>Admin was unlocked while a caller waits for it: close the unlock dialog and let the caller go on.</summary>
    private void OnAdminTokenChanged(AdminUnlockResponse? unlock)
    {
        if (unlock is null || _adminUnlock is not { Task.IsCompleted: false } pending)
        {
            return;
        }

        _adminUnlock = null;
        ReleasePointerState();
        ActiveDialog = null;
        pending.TrySetResult(true);
    }

    private void OnChangeNumberDialogClosed((bool Confirmed, string NewNumber, string? Reason) result)
    {
        var pending = _changeNumber;
        _changeNumber = null;
        if (ActiveDialog == ShellDialogs.ChangeNumber) ActiveDialog = null;
        pending?.TrySetResult(result);
    }

    private void OnReasonPromptDialogClosed(string? result)
    {
        var pending = _reason;
        _reason = null;
        if (ActiveDialog == ShellDialogs.ReasonPrompt) ActiveDialog = null;
        pending?.TrySetResult(result);
    }

    private static void ReleasePointerState()
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
