using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.Tests.Views;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// New bill (the button beside Print estimate, and Ctrl+Shift+N) leaves the bill on the
/// form and starts an empty one. It asks first only when that would lose something not
/// saved, and a "no", or no one to ask, keeps the form as it is.
/// </summary>
public sealed class InvoiceNewBillTests
{
    private const string OpenedNumber = "SR/25-26/0001";

    [Fact]
    public void An_empty_form_has_nothing_to_start_over_from()
    {
        var vm = new InvoiceViewModel(new FakeBillsApi(), null, null);

        Assert.False(vm.NewBillCommand.CanExecute(null));
        Assert.Equal("Clear", vm.NewBillLabel);
    }

    [Fact]
    public async Task An_opened_bill_with_no_changes_is_left_without_asking()
    {
        var (vm, questions) = await OpenedBillAsync(answer: false);
        var started = 0;
        vm.NewBillStarted += (_, _) => started++;
        Assert.Equal("New bill", vm.NewBillLabel);
        Assert.True(vm.NewBillCommand.CanExecute(null));

        await vm.NewBillCommand.ExecuteAsync(null);

        Assert.Empty(questions);
        AssertEmptyNewBill(vm);
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task Unsaved_changes_to_an_opened_bill_are_kept_when_the_operator_says_no()
    {
        var (vm, questions) = await OpenedBillAsync(answer: false);
        vm.PartyName = "Changed Customer";

        await vm.NewBillCommand.ExecuteAsync(null);

        var question = Assert.Single(questions);
        Assert.Equal($"Discard your changes to {OpenedNumber}?", question.Title);
        Assert.True(question.IsWarning);
        Assert.Equal("Changed Customer", vm.PartyName);
        Assert.Equal(BillStates.Pending, vm.CurrentBillState);
        Assert.Equal(OpenedNumber, vm.InvoiceNumber);
        Assert.Equal(1, vm.ItemCount);
    }

    [Fact]
    public async Task Unsaved_changes_to_an_opened_bill_are_discarded_when_the_operator_says_yes()
    {
        var (vm, questions) = await OpenedBillAsync(answer: true);
        vm.PartyName = "Changed Customer";

        await vm.NewBillCommand.ExecuteAsync(null);

        Assert.Single(questions);
        AssertEmptyNewBill(vm);
    }

    [Fact]
    public async Task A_typed_bill_not_yet_saved_is_discarded_only_after_asking()
    {
        var questions = new List<ConfirmRequest>();
        var vm = new InvoiceViewModel(new FakeBillsApi(), null, null)
        {
            ConfirmHandler = request =>
            {
                questions.Add(request);
                return Task.FromResult(true);
            }
        };
        vm.PartyName = "Walk-in Customer";
        vm.Lines[0].ItemName = "Gold Coin";
        vm.Lines[0].GrossWeight = 2m;

        await vm.NewBillCommand.ExecuteAsync(null);

        Assert.Equal("Discard this bill?", Assert.Single(questions).Title);
        AssertEmptyNewBill(vm);
    }

    [Fact]
    public async Task With_no_one_to_ask_unsaved_changes_are_kept()
    {
        var vm = new InvoiceViewModel(new FakeBillsApi(), null, null);
        vm.PartyName = "Walk-in Customer";

        await vm.NewBillCommand.ExecuteAsync(null);

        Assert.Equal("Walk-in Customer", vm.PartyName);
    }

    private static void AssertEmptyNewBill(InvoiceViewModel vm)
    {
        Assert.Null(vm.CurrentBillState);
        Assert.False(vm.IsInvoiceNumberAssigned);
        Assert.Equal(string.Empty, vm.PartyName);
        Assert.Equal(0, vm.ItemCount);
        Assert.Single(vm.Lines);
        Assert.Equal("Clear", vm.NewBillLabel);
        Assert.Equal("New bill", vm.StatusText);
    }

    private static async Task<(InvoiceViewModel Vm, List<ConfirmRequest> Questions)> OpenedBillAsync(bool answer)
    {
        var billId = Guid.NewGuid();
        var payload = new BillPayloadDto(
            PartyName: "Customer",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: new DateOnly(2026, 4, 24),
            Lines: [new BillLineItemDto("Diamond Ring", "7113", 2m, ItemUnits.Carat, 3500m, 7000m, null, null)],
            Totals: new BillTotalsDto(7000m, 0m, 0m, 0m, 7000m),
            Notes: null,
            Payment: "Cash",
            Rate24Kt: null);
        var api = new FakeBillsApi { GetResponse = FakeBillsApi.Bill(billId, BillStates.Pending, payload, OpenedNumber) };
        var questions = new List<ConfirmRequest>();
        var vm = new InvoiceViewModel(api, null, null)
        {
            ConfirmHandler = request =>
            {
                questions.Add(request);
                return Task.FromResult(answer);
            }
        };
        await vm.LoadBillForEditAsync(billId);
        return (vm, questions);
    }
}

/// <summary>Ctrl+Shift+N is a window key binding, so the shell decides where it may act.</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class NewBillShortcutTests
{
    [Fact]
    public void Ctrl_Shift_N_asks_on_the_Invoice_tab_and_does_nothing_on_another()
    {
        var (askedOnBills, question, itemsAfter) = WpfTestHost.Run(() =>
        {
            // The sample shell holds a typed bill that has not been saved, and
            // leaves a confirm question open for its snapshot; answer that first.
            var shell = DesignSample.CreateShell();
            shell.ConfirmDialog.Cancel();

            shell.ActiveTab = NavTab.Bills;
            shell.NewInvoiceBillCommand.Execute(null);
            var askedOnBills = shell.ConfirmDialog.IsOpen;

            shell.ActiveTab = NavTab.Invoice;
            shell.NewInvoiceBillCommand.Execute(null);
            var question = shell.ConfirmDialog.IsOpen ? shell.ConfirmDialog.TitleText : null;
            shell.ConfirmDialog.ConfirmCommand.Execute(null);
            WpfTestHost.DrainDispatcher();

            return (askedOnBills, question, shell.Invoice.ItemCount);
        });

        Assert.False(askedOnBills);
        Assert.Equal("Discard this bill?", question);
        Assert.Equal(0, itemsAfter);
    }
}
