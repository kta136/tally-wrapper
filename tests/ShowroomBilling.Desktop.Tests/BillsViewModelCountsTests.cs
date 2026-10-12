using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

public sealed class BillsViewModelCountsTests
{
    private static BillsViewModel WithBills(params string[] states)
    {
        var vm = new BillsViewModel { HideFullyPostedDays = false };
        var created = DateTimeOffset.UtcNow;
        var number = 0;
        foreach (var state in states)
        {
            number++;
            vm.Items.Add(new BillListRowViewModel(new BillSummaryItem(
                Guid.NewGuid(), state, $"SR/2026-27/{number:0000}", "A", new DateOnly(2026, 10, 11), 100m, created, created)));
        }

        return vm;
    }

    [Fact]
    public void Pending_and_draft_bills_are_the_ones_waiting_for_Tally()
    {
        var vm = WithBills(BillStates.Pending, BillStates.Draft, BillStates.Posted, BillStates.Posting);

        Assert.Equal(2, vm.WaitingForTallyCount);
        Assert.Equal("2 waiting for Tally", vm.WaitingForTallyText);
        Assert.True(vm.HasWaitingForTally);
    }

    [Fact]
    public void Failed_and_reconcile_bills_need_attention_and_a_push_in_flight_is_counted_apart()
    {
        var vm = WithBills(BillStates.Failed, BillStates.ReconciliationRequired, BillStates.Posting, BillStates.Posted);

        Assert.Equal(2, vm.NeedAttentionCount);
        Assert.Equal("2 need attention", vm.NeedAttentionText);
        Assert.Equal(1, vm.PostingCount);
        Assert.Equal("1 posting", vm.PostingText);
    }

    [Fact]
    public void Nothing_to_do_means_no_counts()
    {
        var vm = WithBills(BillStates.Posted, BillStates.Voided);

        Assert.False(vm.HasWaitingForTally);
        Assert.False(vm.HasPosting);
        Assert.False(vm.HasNeedAttention);
    }

    [Fact]
    public void The_select_all_box_is_ticked_for_all_dashed_for_some_and_empty_for_none()
    {
        var vm = WithBills(BillStates.Pending, BillStates.Posted);

        Assert.False(vm.AreAllVisibleSelected);

        vm.Items[0].IsSelected = true;
        Assert.Null(vm.AreAllVisibleSelected);

        vm.Items[1].IsSelected = true;
        Assert.True(vm.AreAllVisibleSelected);

        vm.AreAllVisibleSelected = false;
        Assert.False(vm.AreAllVisibleSelected);
        Assert.Equal(0, vm.SelectedCount);
    }
}
