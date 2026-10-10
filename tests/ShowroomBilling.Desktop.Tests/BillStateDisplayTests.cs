using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

public sealed class BillStateDisplayTests
{
    [Theory]
    [InlineData(BillStates.Posted, "ok", "● Posted")]
    [InlineData(BillStates.Pending, "warn", "◐ Pending")]
    [InlineData(BillStates.Posting, "warn", "◐ Posting")]
    [InlineData(BillStates.Draft, "info", "○ Draft")]
    [InlineData(BillStates.Failed, "err", "✕ Failed")]
    [InlineData(BillStates.ReconciliationRequired, "err", "! Reconcile")]
    [InlineData(BillStates.Revised, "neutral", "↻ Revised")]
    [InlineData(BillStates.Voided, "neutral", "— Voided")]
    public void Each_state_has_a_tone_and_a_label(string state, string tone, string label)
    {
        Assert.Equal(tone, BillStateDisplay.Tone(state));
        Assert.Equal(label, BillStateDisplay.Label(state, editedAfterPush: false));
    }

    [Fact]
    public void A_bill_edited_after_its_push_says_so()
    {
        Assert.Equal("◐ Pending · edit", BillStateDisplay.Label(BillStates.Pending, editedAfterPush: true));
    }

    [Fact]
    public void The_list_row_and_the_details_dialog_show_the_same_chip()
    {
        var created = DateTimeOffset.UtcNow;
        var row = new BillListRowViewModel(new BillSummaryItem(
            Guid.NewGuid(), BillStates.ReconciliationRequired, "SR/2026-27/0007", "A", null, 100m, created, created));

        Assert.Equal("! Reconcile", row.StateChipLabel);
        Assert.Equal("err", row.StateTone);

        // No bill loaded yet: a neutral dash, not a blank chip.
        var details = new BillDetailsViewModel();
        Assert.Equal("—", details.StateChipLabel);
        Assert.Equal("neutral", details.StateTone);
    }
}
