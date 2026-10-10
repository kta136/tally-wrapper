using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>The Quick Add pane and the line grid's remove-row rule.</summary>
public sealed class InvoiceQuickAddTests
{
    [Fact]
    public void Results_list_the_named_items_and_narrow_as_the_operator_types()
    {
        var (vm, _) = InvoiceWith("22K Ring", "22K Chain", "Silver Coin", "   ");

        Assert.Equal(["22K Ring", "22K Chain", "Silver Coin"], vm.QuickAddResults.Select(x => x.Name));

        vm.QuickAddQuery = "  chain ";

        Assert.Equal(["22K Chain"], vm.QuickAddResults.Select(x => x.Name));
        Assert.Equal("22K Chain", vm.QuickAddSelection?.Name);
    }

    [Fact]
    public void Results_follow_the_item_master_list_as_it_changes()
    {
        var (vm, settings) = InvoiceWith("22K Ring");

        settings.Draft.ItemMasterRows.Add(new ItemMasterRowVm { Name = "Silver Coin" });
        Assert.Equal(["22K Ring", "Silver Coin"], vm.QuickAddResults.Select(x => x.Name));

        settings.Draft.ItemMasterRows[0].Name = "22K Bangle";
        Assert.Equal(["22K Bangle", "Silver Coin"], vm.QuickAddResults.Select(x => x.Name));

        settings.Draft.ItemMasterRows.RemoveAt(1);
        Assert.Equal(["22K Bangle"], vm.QuickAddResults.Select(x => x.Name));
    }

    [Fact]
    public void Committing_puts_the_item_on_the_first_empty_line_and_clears_the_search()
    {
        var (vm, settings) = InvoiceWith("22K Ring", "Silver Coin");
        BillLineViewModel? announced = null;
        vm.QuickAddCommitted += (_, line) => announced = line;
        vm.QuickAddQuery = "coin";

        vm.QuickAddCommitCommand.Execute(null);

        Assert.Same(settings.Draft.ItemMasterRows[1], vm.Lines[0].ItemMaster);
        Assert.Equal("Silver Coin", vm.Lines[0].ItemName);
        Assert.Same(vm.Lines[0], announced);
        Assert.Equal(string.Empty, vm.QuickAddQuery);
        // The grid keeps one blank row after the last used one.
        Assert.Equal(2, vm.Lines.Count);
        Assert.True(vm.Lines[^1].IsEmpty);
    }

    [Fact]
    public void Committing_with_nothing_to_pick_does_nothing()
    {
        var (vm, _) = InvoiceWith();
        var raised = false;
        vm.QuickAddCommitted += (_, _) => raised = true;

        vm.QuickAddCommitCommand.Execute(null);

        Assert.False(raised);
        Assert.Single(vm.Lines);
    }

    [Fact]
    public void Removing_a_row_keeps_one_blank_row_at_the_end_and_focus_in_place()
    {
        var (vm, settings) = InvoiceWith("22K Ring", "Silver Coin");
        vm.QuickAddCommitCommand.Execute(settings.Draft.ItemMasterRows[0]);
        vm.QuickAddCommitCommand.Execute(settings.Draft.ItemMasterRows[1]);
        Assert.Equal(3, vm.Lines.Count);

        vm.RemoveLineCommand.Execute(vm.Lines[0]);

        Assert.Equal(["Silver Coin", string.Empty], vm.Lines.Select(l => l.ItemName));
        Assert.Equal(0, vm.FocusedRowIndex);

        vm.FocusedRowIndex = 0;
        vm.RemoveFocusedRowCommand.Execute(null);

        Assert.Single(vm.Lines);
        Assert.True(vm.Lines[0].IsEmpty);
    }

    private static (InvoiceViewModel Vm, SettingsViewModel Settings) InvoiceWith(params string[] itemNames)
    {
        var settings = new SettingsViewModel();
        foreach (var name in itemNames)
        {
            settings.Draft.ItemMasterRows.Add(new ItemMasterRowVm { Name = name });
        }

        return (new InvoiceViewModel(null, null, settings), settings);
    }
}
