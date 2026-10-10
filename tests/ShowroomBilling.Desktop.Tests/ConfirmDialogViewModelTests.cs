using ShowroomBilling.Desktop.ViewModels;

namespace ShowroomBilling.Desktop.Tests;

public sealed class ConfirmDialogViewModelTests
{
    [Fact]
    public async Task Asking_opens_the_dialog_and_confirm_answers_yes()
    {
        var dialog = new ConfirmDialogViewModel();

        var answer = dialog.ShowAsync(new ConfirmRequest("Confirm Void", "Void SR/1?", "Void", IsWarning: true));

        Assert.True(dialog.IsOpen);
        Assert.Equal("Confirm Void", dialog.TitleText);
        Assert.Equal("Void SR/1?", dialog.MessageText);
        Assert.Equal("Void", dialog.ConfirmText);
        Assert.True(dialog.IsWarning);
        Assert.False(answer.IsCompleted);

        dialog.ConfirmCommand.Execute(null);

        Assert.True(await answer);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task Cancel_and_escape_both_answer_no()
    {
        var dialog = new ConfirmDialogViewModel();

        var cancelled = dialog.ShowAsync(new ConfirmRequest("A", "a"));
        dialog.CancelCommand.Execute(null);
        var escaped = dialog.ShowAsync(new ConfirmRequest("B", "b"));
        dialog.Cancel();

        Assert.False(await cancelled);
        Assert.False(await escaped);
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public async Task A_question_asked_while_another_is_open_is_answered_no_and_leaves_the_first_alone()
    {
        var dialog = new ConfirmDialogViewModel();
        var first = dialog.ShowAsync(new ConfirmRequest("First", "first"));

        var second = dialog.ShowAsync(new ConfirmRequest("Second", "second"));

        Assert.False(await second);
        Assert.Equal("First", dialog.TitleText);
        Assert.True(dialog.IsOpen);

        dialog.ConfirmCommand.Execute(null);
        Assert.True(await first);
    }

    [Fact]
    public void Closing_with_nothing_asked_does_nothing()
    {
        var dialog = new ConfirmDialogViewModel();

        dialog.Cancel();
        dialog.ConfirmCommand.Execute(null);

        Assert.False(dialog.IsOpen);
    }
}
