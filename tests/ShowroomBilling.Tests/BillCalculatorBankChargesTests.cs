using ShowroomBilling.Application.Bills;

namespace ShowroomBilling.Tests;

/// <summary>
/// Bank charges are added after GST, as the discount is taken off after it, and the Final amount
/// box's discount brings the bill, bank charges included, to the amount typed.
/// </summary>
public sealed class BillCalculatorBankChargesTests
{
    [Fact]
    public void Bank_charges_are_added_after_gst_and_leave_the_gst_alone()
    {
        var without = BillCalculator.BuildTotals(1000m, discount: 0m);
        var with = BillCalculator.BuildTotals(1000m, discount: 0m, bankCharges: 50.40m);

        Assert.Equal(970.87m, with.SubtotalBase);
        Assert.Equal(without.Cgst, with.Cgst);
        Assert.Equal(without.Sgst, with.Sgst);
        Assert.Equal(50.40m, with.BankCharges);
        // 970.87 + 14.56 + 14.56 + 50.40 = 1,050.39, to the rupee.
        Assert.Equal(1050m, with.GrandTotal);
        Assert.Equal(-0.39m, with.RoundOff);
    }

    [Fact]
    public void Discount_and_bank_charges_together_balance_to_the_paisa()
    {
        var totals = BillCalculator.BuildTotals(1000m, discount: 100.25m, bankCharges: 50.40m);

        Assert.Equal(
            totals.GrandTotal,
            totals.SubtotalBase + totals.Cgst + totals.Sgst - totals.Discount + totals.BankCharges + totals.RoundOff);
    }

    [Fact]
    public void A_bill_without_bank_charges_comes_to_what_it_did_before()
    {
        Assert.Equal(BillCalculator.BuildTotals(2_94_705.68m, 5_000m), BillCalculator.BuildTotals(2_94_705.68m, 5_000m, 0m));
        Assert.Equal(0m, BillCalculator.BuildTotals(2_94_705.68m, 5_000m).BankCharges);
    }

    [Fact]
    public void The_final_amount_includes_the_bank_charges()
    {
        var discount = BillCalculator.FinalAmountDiscount(1000m, finalAmount: 1000m, bankCharges: 50.40m);

        Assert.Equal(50.39m, discount);
        var totals = BillCalculator.BuildTotals(1000m, discount, bankCharges: 50.40m);
        Assert.Equal(1000m, totals.GrandTotal);
        Assert.Equal(0m, totals.RoundOff);
    }

    [Theory]
    [InlineData(1050)]
    [InlineData(1100)]
    public void A_final_amount_at_or_above_the_total_with_bank_charges_takes_nothing_off(int finalAmount)
    {
        Assert.Equal(0m, BillCalculator.FinalAmountDiscount(1000m, finalAmount, bankCharges: 50.40m));
    }
}
