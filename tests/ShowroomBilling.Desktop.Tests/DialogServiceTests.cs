using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The dialog service holds which dialog is open and the prompts a caller
/// awaits. A prompt nobody can answer any more must still complete, or its
/// caller waits for ever and the next prompt is refused as "already open".
/// </summary>
public sealed class DialogServiceTests
{
    private readonly AdminTokenStore _tokens = new();
    private readonly DialogService _dialogs;

    public DialogServiceTests()
    {
        _dialogs = new DialogService(_tokens, new AdminUnlockViewModel(new FakeAdminApi(), _tokens));
    }

    [Fact]
    public async Task An_unlock_request_opens_the_dialog_and_completes_when_admin_is_unlocked()
    {
        var waiting = _dialogs.RequestAdminUnlockAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ShellDialogs.Admin, _dialogs.ActiveDialog);
        Assert.False(waiting.IsCompleted);

        _tokens.Set(Unlocked());
        await waiting;

        Assert.Null(_dialogs.ActiveDialog);
    }

    [Fact]
    public async Task An_unlock_request_completes_when_the_dialog_is_closed_without_unlocking()
    {
        var waiting = _dialogs.RequestAdminUnlockAsync(TestContext.Current.CancellationToken);

        _dialogs.Close(ShellDialogs.Admin);
        await waiting;

        Assert.False(_tokens.IsUnlocked);
    }

    [Fact]
    public async Task Nothing_opens_when_admin_is_already_unlocked()
    {
        _tokens.Set(Unlocked());

        await _dialogs.RequestAdminUnlockAsync(TestContext.Current.CancellationToken);

        Assert.Null(_dialogs.ActiveDialog);
    }

    [Fact]
    public async Task A_reason_is_returned_on_OK_and_null_on_cancel()
    {
        var first = _dialogs.PromptReasonAsync(Request(), TestContext.Current.CancellationToken);
        Assert.Equal(ShellDialogs.ReasonPrompt, _dialogs.ActiveDialog);
        _dialogs.ReasonPromptDialog.ReasonText = "checked in Tally";
        _dialogs.ReasonPromptDialog.OkCommand.Execute(null);

        Assert.Equal("checked in Tally", await first);
        Assert.Null(_dialogs.ActiveDialog);

        var second = _dialogs.PromptReasonAsync(Request(), TestContext.Current.CancellationToken);
        _dialogs.ReasonPromptDialog.CancelCommand.Execute(null);

        Assert.Null(await second);
    }

    [Fact]
    public async Task Esc_cancels_the_prompt_that_is_open()
    {
        var reason = _dialogs.PromptReasonAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(_dialogs.CancelPrompt());
        Assert.Null(await reason);
        Assert.Null(_dialogs.ActiveDialog);

        var number = _dialogs.PromptChangeNumberAsync(Prompt());
        Assert.Equal(ShellDialogs.ChangeNumber, _dialogs.ActiveDialog);
        Assert.True(_dialogs.CancelPrompt());
        Assert.False((await number).Confirmed);

        // Any other dialog is not a prompt: Esc is left to the shell.
        _dialogs.Show(ShellDialogs.Shortcuts);
        Assert.False(_dialogs.CancelPrompt());
        Assert.Equal(ShellDialogs.Shortcuts, _dialogs.ActiveDialog);
    }

    [Fact]
    public async Task A_prompt_replaced_by_another_dialog_is_answered_as_cancelled()
    {
        var number = _dialogs.PromptChangeNumberAsync(Prompt());

        _dialogs.Show(ShellDialogs.Health);

        Assert.False((await number).Confirmed);

        // And the next one is not refused as "already open".
        var again = _dialogs.PromptChangeNumberAsync(Prompt());
        Assert.Equal(ShellDialogs.ChangeNumber, _dialogs.ActiveDialog);
        _dialogs.ChangeNumberDialog.NewNumberRaw = "42";
        _dialogs.ChangeNumberDialog.OkCommand.Execute(null);

        var answer = await again;
        Assert.True(answer.Confirmed);
        Assert.Equal("42", answer.NewNumber);
    }

    [Fact]
    public async Task A_second_prompt_while_one_is_open_is_declined_and_the_first_stays()
    {
        var first = _dialogs.PromptReasonAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Null(await _dialogs.PromptReasonAsync(Request(), TestContext.Current.CancellationToken));
        Assert.False(first.IsCompleted);
        Assert.Equal(ShellDialogs.ReasonPrompt, _dialogs.ActiveDialog);
    }

    [Fact]
    public async Task A_question_goes_to_the_confirm_overlay_and_does_not_close_the_dialog_under_it()
    {
        _dialogs.Show(ShellDialogs.BillDetails);

        var answer = _dialogs.ConfirmAsync(new ConfirmRequest("Void this bill?", "It cannot be undone.", "Void"));
        Assert.True(_dialogs.ConfirmDialog.IsOpen);
        _dialogs.ConfirmDialog.ConfirmCommand.Execute(null);

        Assert.True(await answer);
        Assert.Equal(ShellDialogs.BillDetails, _dialogs.ActiveDialog);
    }

    [Fact]
    public void Close_leaves_another_dialog_alone()
    {
        _dialogs.Show(ShellDialogs.Health);

        _dialogs.Close(ShellDialogs.HandoverKey);

        Assert.Equal(ShellDialogs.Health, _dialogs.ActiveDialog);
    }

    private static ReasonRequest Request() => new("Mark as pushed", "one bill", "This marks it as posted.", "Mark as pushed");

    private static ChangeNumberPrompt Prompt() => new("SR/26-27/0007", "SR/", null, "26-27", 4, "Walk-in Customer");

    private static AdminUnlockResponse Unlocked() =>
        new("admin-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "test");

    private sealed class FakeAdminApi : IAdminApiClient
    {
        public Task<AdminPasscodeStatusResponse> GetPasscodeStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AdminPasscodeStatusResponse(true));

        public Task SetPasscodeAsync(AdminSetPasscodeRequest request, string? adminToken = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<AdminUnlockResponse> UnlockAsync(AdminUnlockRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Unlocked());

        public Task LogoutAsync(AdminLogoutRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
