namespace ShowroomBilling.Contracts.Bills;

/// <summary>
/// GST facts for jewellery sales, shared by pricing, the printed invoice and
/// the Tally voucher so the three cannot drift apart.
/// </summary>
public static class JewelleryGst
{
    /// <summary>HSN code for articles of jewellery; used when a line carries none.</summary>
    public const string HsnCode = "711319";

    public const decimal CgstRate = 0.015m;
    public const decimal SgstRate = 0.015m;

    /// <summary>
    /// Splits a bill's tax total into its CGST and SGST legs. The printed
    /// invoice and the Tally voucher must agree to the paisa, so both call
    /// this: CGST takes the odd half-paisa and SGST is the remainder.
    /// </summary>
    public static (decimal Cgst, decimal Sgst) Split(decimal taxTotal)
    {
        var cgst = Math.Round(taxTotal / 2m, 2, MidpointRounding.AwayFromZero);
        return (cgst, taxTotal - cgst);
    }
}
