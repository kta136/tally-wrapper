using ShowroomBilling.Printing;

namespace ShowroomBilling.Tests;

public class PreviewSampleProviderTests
{
    private static readonly CompanyProfile AnyCompany = new(
        Name: "Preview Co",
        Address: null,
        Phone: null,
        State: null,
        Country: null,
        Gstin: string.Empty,
        Huid: null,
        BankName: null,
        BankAccount: null,
        BankIfsc: null,
        BankUpi: null,
        TermsAndConditions: null);

    [Fact]
    public void Sample_exercises_HSN_gross_less_extra_and_bare_line()
    {
        var content = PreviewSampleProvider.CreateSampleContent(AnyCompany);

        // First line drives the conditional Gross/Less/Extra columns on. Second line
        // is a bare item so the code paths that skip those columns also exercise.
        Assert.Equal(2, content.Lines.Count);

        var rich = content.Lines[0];
        Assert.True(rich.HsnCode is null, "first line leaves HSN null so the 711319 fallback renders");
        Assert.True((rich.GrossWeight ?? 0m) > 0m);
        Assert.True((rich.LessWeight ?? 0m) > 0m);
        Assert.True((rich.Extra ?? 0m) > 0m);
        Assert.True((rich.WastagePercent ?? 0m) > 0m);
        Assert.True((rich.LabourPerUnit ?? 0m) > 0m);

        var bare = content.Lines[1];
        Assert.False(string.IsNullOrEmpty(bare.HsnCode));
        Assert.Null(bare.GrossWeight);
        Assert.Null(bare.LessWeight);
        Assert.Null(bare.Extra);
    }

    [Fact]
    public void Sample_totals_include_discount_and_round_off()
    {
        var content = PreviewSampleProvider.CreateSampleContent(AnyCompany);
        Assert.True(content.Totals.DiscountTotal > 0m, "discount row should render in the summary");
        Assert.NotEqual(0m, content.Totals.RoundOff);
        Assert.True(content.Totals.TaxTotal > 0m, "GST breakup box needs a non-zero tax total");
    }

    [Fact]
    public void Sample_carries_provided_company_profile_unchanged()
    {
        var content = PreviewSampleProvider.CreateSampleContent(AnyCompany);
        Assert.Same(AnyCompany, content.Company);
    }
}
