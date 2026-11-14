using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>
/// The shell's dialogs for a test. Left alone it answers as an operator who
/// declines everything: "no" to a question, no passcode, no reason, no new
/// number. A test that needs another answer sets the matching function, and
/// reads <see cref="Questions"/> or <see cref="Shown"/> for what was asked.
/// </summary>
internal sealed class FakeDialogs : IDialogService
{
    public Func<ConfirmRequest, Task<bool>> Confirm { get; set; } = _ => Task.FromResult(false);

    public Func<CancellationToken, Task> AdminUnlock { get; set; } = _ => Task.CompletedTask;

    public Func<ReasonRequest, CancellationToken, Task<string?>> Reason { get; set; } = (_, _) => Task.FromResult<string?>(null);

    public Func<ChangeNumberPrompt, Task<(bool Confirmed, string NewNumber, string? Reason)>> ChangeNumber { get; set; }
        = _ => Task.FromResult((false, string.Empty, (string?)null));

    /// <summary>Every yes/no question asked, in order.</summary>
    public List<ConfirmRequest> Questions { get; } = [];

    /// <summary>The named dialogs opened and not closed again.</summary>
    public List<string> Shown { get; } = [];

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Questions.Add(request);
        return Confirm(request);
    }

    public Task RequestAdminUnlockAsync(CancellationToken cancellationToken) => AdminUnlock(cancellationToken);

    public Task<string?> PromptReasonAsync(ReasonRequest request, CancellationToken cancellationToken) => Reason(request, cancellationToken);

    public Task<(bool Confirmed, string NewNumber, string? Reason)> PromptChangeNumberAsync(ChangeNumberPrompt prompt) => ChangeNumber(prompt);

    public void Show(string dialog) => Shown.Add(dialog);

    public void Close(string dialog) => Shown.Remove(dialog);
}
