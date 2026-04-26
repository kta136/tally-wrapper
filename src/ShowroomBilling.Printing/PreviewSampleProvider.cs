using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Printing;

/// <summary>
/// Deterministic sample invoice content used to render the live Settings preview.
/// Must exercise every optional print column — HSN, gross/less weight, extra
/// charges, making (wastage + labour), bank block, terms — so operator changes to
/// those fields become visible in the preview without needing a real saved bill.
/// </summary>
public static class PreviewSampleProvider
{
    /// <summary>
    /// Build sample content scoped to the operator's company profile.
    /// The company profile is taken from live settings so the preview reflects the
    /// user's unsaved edits; all other data is fixed.
    /// </summary>
    public static BillPrintContent CreateSampleContent(CompanyProfile company)
    {
        ArgumentNullException.ThrowIfNull(company);
        return new BillPrintContent(
            InvoiceNumber: "PREVIEW/001",
            BillDate: new DateOnly(2026, 4, 23),
            PartyName: "Sample Customer",
            PartyGstin: "29AAAA0000A1Z5",
            PartyPhone: "+91 98765 43210",
            PartyAddress: "12, MG Road, Bengaluru",
            Payment: "Credit and debit",
            Rate24Kt: 72500m,
            Lines: new[]
            {
                new BillLineItemDto(
                    ItemName: "Gold Necklace",
                    HsnCode: null, // exercises the 711319 fallback
                    Quantity: 22.345m,
                    Unit: "g",
                    Rate: 6550m,
                    LineTotal: 146359.75m,
                    Karat: "22K",
                    RawJson: null,
                    GrossWeight: 22.800m,
                    LessWeight: 0.455m,     // triggers Gross/Less columns
                    WastagePercent: 12.50m,
                    LabourPerUnit: 150m,
                    Extra: 500m),           // triggers Extra column
                new BillLineItemDto(
                    ItemName: "Diamond Ring",
                    HsnCode: "7113",
                    Quantity: 3.500m,
                    Unit: "g",
                    Rate: 45000m,
                    LineTotal: 157500m,
                    Karat: "18K",
                    RawJson: null),
            },
            Totals: new BillTotalsDto(
                Subtotal: 303859.75m,
                DiscountTotal: 3000m,
                TaxTotal: 9115.79m,
                RoundOff: -0.54m,
                GrandTotal: 309975m),
            Notes: "Preview sample — not a real invoice",
            Company: company);
    }
}
