using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Tests;

/// <summary>
/// The rules a client cannot forge. Line totals are GST-inclusive, so the
/// valid baseline has lines summing to 103,000 against a subtotal of 100,000
/// plus 3,000 tax.
/// </summary>
public sealed class BillValidatorTests
{
    private static readonly DateOnly Today = IndiaTime.Today();

    [Fact]
    public void A_balanced_bill_is_accepted()
    {
        BillValidator.Validate(Bill());
    }

    [Fact]
    public void A_balanced_bill_with_discount_and_round_off_is_accepted()
    {
        // 103,000.40 of lines, 1,000 off, rounded down by 0.40.
        var payload = Bill(
            lines: [Line(lineTotal: 103_000.40m)],
            totals: new BillTotalsDto(Subtotal: 100_000.40m, DiscountTotal: 1_000m, TaxTotal: 3_000m, RoundOff: -0.40m, GrandTotal: 102_000m));

        BillValidator.Validate(payload);
    }

    [Fact]
    public void Line_total_need_not_equal_rate_times_quantity()
    {
        // Wastage, labour and extras make the line total more than rate x weight.
        BillValidator.Validate(Bill(lines: [Line(quantity: 10m, rate: 9_000m, lineTotal: 103_000m)]));
    }

    [Fact]
    public void Lines_that_do_not_add_up_to_what_the_customer_is_billed_are_rejected()
    {
        var payload = Bill(lines: [Line(lineTotal: 50_000m), Line(lineTotal: 40_000m)]);

        AssertRejected(payload, "Sum of line totals");
    }

    [Fact]
    public void An_inflated_grand_total_is_rejected_even_when_the_lines_match_it()
    {
        // Lines agree with the grand total, but subtotal + tax does not.
        var payload = Bill(
            lines: [Line(lineTotal: 150_000m)],
            totals: new BillTotalsDto(Subtotal: 100_000m, DiscountTotal: 0m, TaxTotal: 3_000m, RoundOff: 0m, GrandTotal: 150_000m));

        AssertRejected(payload, "does not balance");
    }

    [Fact]
    public void A_discount_that_was_not_taken_off_the_grand_total_is_rejected()
    {
        var payload = Bill(
            totals: new BillTotalsDto(Subtotal: 100_000m, DiscountTotal: 5_000m, TaxTotal: 3_000m, RoundOff: 0m, GrandTotal: 103_000m));

        AssertRejected(payload, "does not balance");
    }

    [Theory]
    [InlineData(1.06)]
    [InlineData(-1.06)]
    public void The_line_sum_may_drift_by_rounding_only(double drift)
    {
        // One line: 0.05 per line + 0.01 + 1.00 of tolerance.
        BillValidator.Validate(Bill(lines: [Line(lineTotal: 103_000m + (decimal)drift)]));
        AssertRejected(
            Bill(lines: [Line(lineTotal: 103_000m + (decimal)drift * 2)]),
            "Sum of line totals");
    }

    [Theory]
    [InlineData(1.01)]
    [InlineData(-1.01)]
    public void Round_off_beyond_one_rupee_is_rejected(double roundOff)
    {
        var amount = (decimal)roundOff;
        var payload = Bill(
            lines: [Line(lineTotal: 103_000m - amount)],
            totals: new BillTotalsDto(Subtotal: 100_000m - amount, DiscountTotal: 0m, TaxTotal: 3_000m, RoundOff: amount, GrandTotal: 103_000m));

        AssertRejected(payload, "|RoundOff| must be <=");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-103_000)]
    public void A_grand_total_that_is_not_positive_is_rejected(int grandTotal)
    {
        var payload = Bill(
            totals: new BillTotalsDto(Subtotal: 100_000m, DiscountTotal: 0m, TaxTotal: 3_000m, RoundOff: 0m, GrandTotal: grandTotal));

        AssertRejected(payload, "GrandTotal must be positive");
    }

    [Fact]
    public void Negative_totals_are_rejected()
    {
        AssertRejected(
            Bill(totals: new BillTotalsDto(Subtotal: 104_000m, DiscountTotal: -1_000m, TaxTotal: 0m, RoundOff: 0m, GrandTotal: 103_000m)),
            "DiscountTotal must be non-negative");
        AssertRejected(
            Bill(totals: new BillTotalsDto(Subtotal: -1m, DiscountTotal: 0m, TaxTotal: 103_001m, RoundOff: 0m, GrandTotal: 103_000m)),
            "Subtotal must be non-negative");
        AssertRejected(
            Bill(totals: new BillTotalsDto(Subtotal: 104_000m, DiscountTotal: 0m, TaxTotal: -1_000m, RoundOff: 0m, GrandTotal: 103_000m)),
            "TaxTotal must be non-negative");
    }

    [Fact]
    public void A_total_with_a_third_decimal_is_rejected()
    {
        var payload = Bill(
            totals: new BillTotalsDto(Subtotal: 100_000m, DiscountTotal: 0.555m, TaxTotal: 3_000m, RoundOff: 0m, GrandTotal: 102_999.445m));

        AssertRejected(payload, "DiscountTotal must have at most two decimal places");
    }

    [Fact]
    public void Line_quantities_and_amounts_must_have_the_right_sign()
    {
        AssertRejected(Bill(lines: [Line(quantity: 0m)]), "Quantity must be positive");
        AssertRejected(Bill(lines: [Line(rate: -1m)]), "Rate must be non-negative");
        AssertRejected(Bill(lines: [Line(lineTotal: -103_000m)]), "LineTotal must be non-negative");
        AssertRejected(Bill(lines: [Line() with { GrossWeight = -1m }]), "GrossWeight must be non-negative");
        AssertRejected(Bill(lines: [Line() with { LessWeight = -1m }]), "LessWeight must be non-negative");
        AssertRejected(Bill(lines: [Line() with { WastagePercent = -1m }]), "WastagePercent must be non-negative");
        AssertRejected(Bill(lines: [Line() with { LabourPerUnit = -1m }]), "LabourPerUnit must be non-negative");
        AssertRejected(Bill(lines: [Line() with { DiamondRate = -1m }]), "DiamondRate must be non-negative");
        AssertRejected(Bill(lines: [Line() with { Extra = -1m }]), "Extra must be non-negative");
    }

    [Fact]
    public void A_line_needs_an_item_name()
    {
        AssertRejected(Bill(lines: [Line() with { ItemName = " " }]), "ItemName is required");
    }

    [Fact]
    public void Bill_date_window_runs_from_a_year_back_to_tomorrow()
    {
        BillValidator.Validate(Bill(billDate: Today.AddYears(-1)));
        BillValidator.Validate(Bill(billDate: Today.AddDays(1)));

        AssertRejected(Bill(billDate: Today.AddYears(-1).AddDays(-1)), "outside the allowed window");
        AssertRejected(Bill(billDate: Today.AddDays(2)), "outside the allowed window");
    }

    [Fact]
    public void A_bill_needs_lines_and_totals()
    {
        AssertRejected(Bill(lines: []), "at least one line item");
        AssertRejected(Bill() with { Totals = null! }, "Totals block is required");
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var payload = Bill(lines: [Line(quantity: 0m, rate: -1m)]);

        var ex = Assert.Throws<BillValidationException>(() => BillValidator.Validate(payload));

        Assert.Equal(2, ex.Errors.Count);
        Assert.StartsWith("Bill payload is invalid: ", ex.Message);
    }

    private static void AssertRejected(BillPayloadDto payload, string expectedFragment)
    {
        var ex = Assert.Throws<BillValidationException>(() => BillValidator.Validate(payload));
        Assert.Contains(ex.Errors, error => error.Contains(expectedFragment, StringComparison.Ordinal));
    }

    private static BillPayloadDto Bill(
        IReadOnlyList<BillLineItemDto>? lines = null,
        BillTotalsDto? totals = null,
        DateOnly? billDate = null) =>
        new(
            PartyName: "Walk-in Customer",
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: billDate ?? Today,
            Lines: lines ?? [Line()],
            Totals: totals ?? new BillTotalsDto(Subtotal: 100_000m, DiscountTotal: 0m, TaxTotal: 3_000m, RoundOff: 0m, GrandTotal: 103_000m),
            Notes: null);

    private static BillLineItemDto Line(
        decimal quantity = 10m,
        decimal rate = 10_300m,
        decimal lineTotal = 103_000m) =>
        new("22K Gold Ring", "711319", quantity, "Gram", rate, lineTotal, "22K", RawJson: null);
}
