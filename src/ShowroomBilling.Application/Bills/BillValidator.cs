using System.Text.Json;
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
    private const int MaxLineItems = 500;
    private const int MaxRawJsonLength = 64 * 1024;

    public static void Validate(BillPayloadDto payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.Lines is null || payload.Lines.Count == 0)
        {
            throw new BillValidationException(new[] { "Bill must have at least one line item." });
        }
        if (payload.Lines.Count > MaxLineItems)
        {
            throw new BillValidationException(new[] { $"Bill cannot contain more than {MaxLineItems} line items." });
        }
        if (payload.Totals is null)
        {
            throw new BillValidationException(new[] { "Totals block is required." });
        }

        var errors = new List<string>();
        AddMaxLength(errors, "PartyName", payload.PartyName, 256);
        AddMaxLength(errors, "PartyGstin", payload.PartyGstin, 32);
        AddMaxLength(errors, "PartyPhone", payload.PartyPhone, 64);
        AddMaxLength(errors, "PartyAddress", payload.PartyAddress, 1_000);
        AddMaxLength(errors, "Notes", payload.Notes, 4_000);
        AddMaxLength(errors, "Payment", payload.Payment, 64);
        if (payload.Rate24Kt < 0m)
        {
            errors.Add($"Rate24Kt must be non-negative (got {payload.Rate24Kt}).");
        }

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
            AddMaxLength(errors, $"{prefix}.ItemName", line.ItemName, 256);
            AddMaxLength(errors, $"{prefix}.StockName", line.StockName, 256);
            AddMaxLength(errors, $"{prefix}.HsnCode", line.HsnCode, 32);
            AddMaxLength(errors, $"{prefix}.Unit", line.Unit, 64);
            AddMaxLength(errors, $"{prefix}.Karat", line.Karat, 32);
            AddMaxLength(errors, $"{prefix}.ItemCategory", line.ItemCategory, 64);
            AddMaxLength(errors, $"{prefix}.PricingMode", line.PricingMode, 64);
            ValidateRawJson(errors, prefix, line.RawJson);
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
            AddNonNegative(errors, prefix, nameof(line.GrossWeight), line.GrossWeight);
            AddNonNegative(errors, prefix, nameof(line.LessWeight), line.LessWeight);
            AddNonNegative(errors, prefix, nameof(line.WastagePercent), line.WastagePercent);
            AddNonNegative(errors, prefix, nameof(line.LabourPerUnit), line.LabourPerUnit);
            AddNonNegative(errors, prefix, nameof(line.DiamondRate), line.DiamondRate);
            AddNonNegative(errors, prefix, nameof(line.Extra), line.Extra);
        }

        var totals = payload.Totals;
        if (totals.DiscountTotal < 0m)
        {
            errors.Add($"DiscountTotal must be non-negative (got {totals.DiscountTotal}).");
        }
        if (totals.Subtotal < 0m)
        {
            errors.Add($"Subtotal must be non-negative (got {totals.Subtotal}).");
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

    private static void AddMaxLength(List<string> errors, string field, string? value, int maxLength)
    {
        if (value?.Length > maxLength)
        {
            errors.Add($"{field} cannot exceed {maxLength} characters.");
        }
    }

    private static void AddNonNegative(
        List<string> errors,
        string prefix,
        string field,
        decimal? value)
    {
        if (value < 0m)
        {
            errors.Add($"{prefix}: {field} must be non-negative (got {value}).");
        }
    }

    private static void ValidateRawJson(List<string> errors, string prefix, string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return;
        }
        if (rawJson.Length > MaxRawJsonLength)
        {
            errors.Add($"{prefix}: RawJson cannot exceed {MaxRawJsonLength} characters.");
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            errors.Add($"{prefix}: RawJson must contain valid JSON.");
        }
    }
}
