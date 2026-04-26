using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Application.Bills;

/// <summary>
/// Server-side validation for client-supplied <see cref="BillPayloadDto"/>. Guards the
/// invariants we can check without replaying jewellery pricing logic (wastage, labour,
/// karat-rate lookups). The goal is that a buggy or hostile client cannot persist
/// a payload whose <c>Totals</c> contradict the line items it sent.
///
/// Domain convention (see CLAUDE.md / docs/15_ui_design_reference): line totals are
/// <b>GST-inclusive</b>; <c>Subtotal</c> is the ex-GST back-calc and <c>TaxTotal</c> is
/// the GST. Therefore <c>Σ LineTotal ≈ Subtotal + TaxTotal</c>, and after discount /
/// round-off: <c>Σ LineTotal ≈ GrandTotal + Discount - RoundOff</c>.
///
/// What we deliberately do NOT check:
/// <list type="bullet">
///   <item><c>LineTotal == Rate * Quantity</c> — pricing modes (wastage %, labour per
///   unit, gross/less weight, diamond rate, extras) mean the line total is a function
///   of several fields, not a simple product.</item>
///   <item><c>TaxTotal / Subtotal ≈ fixed rate</c> — would require hard-coding a GST
///   rate and would break if the domain ever supports mixed rates.</item>
/// </list>
/// </summary>
public static class BillValidator
{
    // Tolerances in rupees. Sized for decimal(18,2) rounding on server-side totals.
    private const decimal LineSumTolerancePerLine = 0.05m;
    private const decimal GrandTotalTolerance = 1.00m;
    private const decimal RoundOffCap = 1.00m;

    public static void Validate(BillPayloadDto payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Lines is null || payload.Lines.Count == 0)
        {
            throw new BillValidationException(new[] { "Bill must have at least one line item." });
        }
        if (payload.Totals is null)
        {
            throw new BillValidationException(new[] { "Totals block is required." });
        }

        var errors = new List<string>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        if (payload.BillDate < today.AddYears(-1) || payload.BillDate > today.AddDays(1))
        {
            errors.Add($"BillDate '{payload.BillDate:yyyy-MM-dd}' is outside the allowed window (today -1 year to +1 day).");
        }

        decimal runningSubtotal = 0m;
        for (var i = 0; i < payload.Lines.Count; i++)
        {
            var line = payload.Lines[i];
            var prefix = $"Line {i + 1}";

            if (line is null)
            {
                errors.Add($"{prefix}: payload is null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(line.ItemName))
            {
                errors.Add($"{prefix}: ItemName is required.");
            }
            if (line.Quantity <= 0m)
            {
                errors.Add($"{prefix}: Quantity must be positive (got {line.Quantity}).");
            }
            if (line.Rate < 0m)
            {
                errors.Add($"{prefix}: Rate must be non-negative (got {line.Rate}).");
            }
            if (line.LineTotal < 0m)
            {
                errors.Add($"{prefix}: LineTotal must be non-negative (got {line.LineTotal}).");
            }
            else
            {
                runningSubtotal += line.LineTotal;
            }
        }

        var totals = payload.Totals;
        if (totals.DiscountTotal < 0m)
        {
            errors.Add($"DiscountTotal must be non-negative (got {totals.DiscountTotal}).");
        }
        if (totals.TaxTotal < 0m)
        {
            errors.Add($"TaxTotal must be non-negative (got {totals.TaxTotal}).");
        }
        if (Math.Abs(totals.RoundOff) > RoundOffCap)
        {
            errors.Add($"|RoundOff| must be <= {RoundOffCap} (got {totals.RoundOff}).");
        }
        if (totals.GrandTotal <= 0m)
        {
            errors.Add($"GrandTotal must be positive (got {totals.GrandTotal}).");
        }

        // Lines (GST-inclusive) must add up to what the customer is billed,
        // modulo discount and round-off.
        var lineSumTolerance = LineSumTolerancePerLine * payload.Lines.Count + 0.01m + GrandTotalTolerance;
        var expectedLineSum = totals.GrandTotal + totals.DiscountTotal - totals.RoundOff;
        if (Math.Abs(runningSubtotal - expectedLineSum) > lineSumTolerance)
        {
            errors.Add(
                $"Sum of line totals {runningSubtotal} does not match GrandTotal + Discount - RoundOff = {expectedLineSum} "
                + $"(tolerance {lineSumTolerance}).");
        }

        // Tax math balances independently of the line-sum check: catches a
        // client that sends an inflated GrandTotal without matching it to
        // Subtotal + Tax (e.g. a buggy client that forgets to apply discount).
        var expectedGrand = totals.Subtotal - totals.DiscountTotal + totals.TaxTotal + totals.RoundOff;
        if (Math.Abs(totals.GrandTotal - expectedGrand) > GrandTotalTolerance)
        {
            errors.Add(
                $"GrandTotal {totals.GrandTotal} does not balance. "
                + $"Expected Subtotal - Discount + Tax + RoundOff = {expectedGrand} "
                + $"(tolerance {GrandTotalTolerance}).");
        }

        if (errors.Count > 0)
        {
            throw new BillValidationException(errors);
        }
    }
}
