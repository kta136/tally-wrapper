using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

public sealed class BillStateDisplayTests
{
    [Theory]
    [InlineData(BillStates.Posted, "ok", "Posted", "dot")]
    [InlineData(BillStates.Pending, "warn", "Pending", "half")]
    [InlineData(BillStates.Posting, "warn", "Posting", "half")]
    [InlineData(BillStates.Draft, "info", "Draft", "ring")]
    [InlineData(BillStates.Failed, "err", "Failed", "x")]
    [InlineData(BillStates.ReconciliationRequired, "err", "Reconcile", "bang")]
    [InlineData(BillStates.Revised, "neutral", "Revised", "redo")]
    [InlineData(BillStates.Voided, "neutral", "Voided", "dash")]
    public void Each_state_has_a_tone_a_word_and_a_shape(string state, string tone, string label, string kind)
    {
        Assert.Equal(tone, BillStateDisplay.Tone(state));
        Assert.Equal(label, BillStateDisplay.Label(state, editedAfterPush: false));
        Assert.Equal(kind, BillStateDisplay.Kind(state));
    }

    [Fact]
    public void No_two_states_share_both_a_word_and_a_shape()
    {
        var states = new[]
        {
            BillStates.Posted, BillStates.Pending, BillStates.Posting, BillStates.Draft, BillStates.Failed,
            BillStates.ReconciliationRequired, BillStates.Revised, BillStates.Voided,
        };

        Assert.Equal(states.Length, states.Select(state => BillStateDisplay.Label(state, false)).Distinct().Count());
    }

    [Fact]
    public void A_bill_edited_after_its_push_says_so()
    {
        Assert.Equal("Pending · edit", BillStateDisplay.Label(BillStates.Pending, editedAfterPush: true));
    }

    [Fact]
    public void The_list_row_and_the_details_dialog_show_the_same_chip()
    {
        var created = DateTimeOffset.UtcNow;
        var row = new BillListRowViewModel(new BillSummaryItem(
            Guid.NewGuid(), BillStates.ReconciliationRequired, "SR/2026-27/0007", "A", null, 100m, created, created));

        Assert.Equal("Reconcile", row.StateChipLabel);
        Assert.Equal("err", row.StateTone);
        Assert.Equal("bang", row.StateKind);

        // No bill loaded yet: a neutral dash, not a blank chip.
        var details = new BillDetailsViewModel();
        Assert.Equal("—", details.StateChipLabel);
        Assert.Equal("neutral", details.StateTone);
        Assert.Equal("none", details.StateKind);
    }

    [Theory]
    [InlineData(BillStates.Failed, "err")]
    [InlineData(BillStates.ReconciliationRequired, "err")]
    [InlineData(BillStates.Pending, "neutral")]
    [InlineData(BillStates.Posted, "neutral")]
    public void A_note_is_red_only_when_something_has_to_be_done_before_the_bill_can_move_on(string state, string noteTone)
    {
        var created = DateTimeOffset.UtcNow;
        var row = new BillListRowViewModel(new BillSummaryItem(Guid.NewGuid(), state, "SR/2026-27/0001", "A", null, 100m, created, created));

        Assert.Equal(noteTone, row.NoteTone);
    }
}
