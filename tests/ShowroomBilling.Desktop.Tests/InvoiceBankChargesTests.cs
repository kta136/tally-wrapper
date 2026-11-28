using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The Bank charges box of the Invoice tab: typed on each bill, added after GST, part of the final
/// amount, saved with the bill and loaded back with it. Every bill here is one diamond line of
/// ₹1,000 with GST (970.87 + 14.56 + 14.56 = 999.99).
/// </summary>
public sealed class InvoiceBankChargesTests
{
    [Fact]
    public void Bank_charges_are_added_to_the_grand_total_and_the_final_amount()
    {
        var vm = InvoiceWithOneThousandRupeeLine();

        vm.BankChargesEnabled = true;
        vm.BankCharges = 50m;

        Assert.Equal(970.87m, vm.Subtotal);
        Assert.Equal(14.56m, vm.Cgst);
        Assert.Equal(1050m, vm.GrandTotal);
        Assert.Equal(1050m, vm.FinalAmount);
        Assert.Equal(0.01m, vm.RoundOff);
    }

    [Fact]
    public void Typing_bank_charges_ticks_the_box_and_unticking_takes_them_off()
    {
        var vm = InvoiceWithOneThousandRupeeLine();

        vm.BankCharges = 50m;
        Assert.True(vm.BankChargesEnabled);
        Assert.Equal(1050m, vm.GrandTotal);

        vm.BankChargesEnabled = false;
        Assert.Equal(0m, vm.BankCharges);
        Assert.Equal(1000m, vm.GrandTotal);
        Assert.Equal(1000m, vm.FinalAmount);
    }

    [Fact]
    public void Negative_bank_charges_are_refused()
    {
        var vm = InvoiceWithOneThousandRupeeLine();

        vm.BankCharges = -20m;

        Assert.Equal(0m, vm.BankCharges);
        Assert.Equal(1000m, vm.GrandTotal);
        Assert.Contains("cannot be negative", vm.SaveStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_final_amount_is_what_the_customer_pays_with_bank_charges()
    {
        var vm = InvoiceWithOneThousandRupeeLine();
        vm.BankCharges = 50m;

        vm.FinalAmount = 1000m;

        // 999.99 + 50 − 1,000.
        Assert.True(vm.DiscountEnabled);
        Assert.Equal(49.99m, vm.Discount);
        Assert.Equal(50m, vm.BankCharges);
        Assert.Equal(1000m, vm.GrandTotal);
        Assert.Equal(1000m, vm.FinalAmount);
    }

    [Fact]
    public void A_final_amount_above_the_total_with_bank_charges_names_that_total()
    {
        var vm = InvoiceWithOneThousandRupeeLine();
        vm.BankCharges = 50m;

        vm.FinalAmount = 1200m;

        Assert.Equal(0m, vm.Discount);
        Assert.Equal(1050m, vm.GrandTotal);
        Assert.Contains("1,050", vm.SaveStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Saving_sends_the_bank_charges_in_totals_that_the_server_accepts()
    {
        var api = new FakeBillsApi();
        var vm = InvoiceWithOneThousandRupeeLine(api);
        vm.BankCharges = 50.25m;
        vm.Discount = 10m;

        await vm.SaveDraftCommand.ExecuteAsync(null);

        var totals = api.LastCreate!.Payload.Totals;
        Assert.Equal(50.25m, totals.BankCharges);
        Assert.Equal(10m, totals.DiscountTotal);
        // 999.99 − 10 + 50.25 = 1,040.24.
        Assert.Equal(1040m, totals.GrandTotal);
        Assert.Equal(-0.24m, totals.RoundOff);
        BillValidator.Validate(api.LastCreate.Payload);
    }

    [Fact]
    public async Task A_bill_with_bank_charges_reopens_with_them_and_Clear_takes_them_off()
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi
        {
            GetResponse = BillWith(billId, new BillTotalsDto(970.87m, 0m, 29.12m, 0.01m, 1050m, BankCharges: 50m)),
        };
        var vm = new InvoiceViewModel(api, FakeNumberingApi.Offline(), new FakeDialogs(), null);

        await vm.LoadBillForEditAsync(billId);

        Assert.True(vm.BankChargesEnabled);
        Assert.Equal(50m, vm.BankCharges);
        Assert.False(vm.DiscountEnabled);
        Assert.Equal(1050m, vm.GrandTotal);

        vm.ClearCommand.Execute(null);

        Assert.False(vm.BankChargesEnabled);
        Assert.Equal(0m, vm.BankCharges);
    }

    [Fact]
    public async Task A_bill_saved_before_bank_charges_reopens_without_them()
    {
        var billId = Guid.NewGuid();
        var api = new FakeBillsApi
        {
            GetResponse = BillWith(billId, new BillTotalsDto(970.87m, 0m, 29.12m, 0.01m, 1000m)),
        };
        var vm = new InvoiceViewModel(api, FakeNumberingApi.Offline(), new FakeDialogs(), null);

        await vm.LoadBillForEditAsync(billId);

        Assert.False(vm.BankChargesEnabled);
        Assert.Equal(0m, vm.BankCharges);
        Assert.Equal(1000m, vm.GrandTotal);
    }

    private static InvoiceViewModel InvoiceWithOneThousandRupeeLine(FakeBillsApi? api = null)
    {
        var vm = new InvoiceViewModel(api ?? new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), null);
        var line = vm.Lines[0];
        line.ItemMaster = TestMasters.Item("Diamond Ring", ItemUnits.Carat, category: ItemCategories.Diamond);
        line.GrossWeight = 10m;
        line.DiamondRate = 100m;
        return vm;
    }

    private static BillResponse BillWith(Guid id, BillTotalsDto totals) =>
        FakeBillsApi.Bill(id, payload: new BillPayloadDto(
            PartyName: "Customer",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: new DateOnly(2026, 4, 24),
            Lines:
            [
                new BillLineItemDto(
                    ItemName: "Diamond Ring",
                    HsnCode: "7113",
                    Quantity: 10m,
                    Unit: ItemUnits.Carat,
                    Rate: 100m,
                    LineTotal: 1000m,
                    Karat: null,
                    RawJson: null,
                    GrossWeight: 10m,
                    LessWeight: 0m,
                    DiamondRate: 100m,
                    ItemCategory: ItemCategories.Diamond,
                    PricingMode: PricingModes.Wastage),
            ],
            Totals: totals,
            Notes: null,
            Payment: "Cash",
            Rate24Kt: null));
}
