using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Tests;

/// <summary>
/// Guards that live-settings-preview composition carries through every
/// print-affecting field the pane observes (company/bank/terms/fonts/layout/assets).
/// The render itself is smoke-tested in <see cref="BillDocumentRenderTests"/>.
/// </summary>
public class ComposePreviewDocumentTests
{
    [Fact]
    public void ComposePreviewDocument_threads_company_and_bank_fields()
    {
        var print = Print(
            companyName: "Acme",
            gstin: "29AAA",
            bankName: "HDFC",
            bankIfsc: "HDFC0000123",
            terms: "T&C apply.");
        var layout = Layout();

        var options = PrintProfileMapping.ComposePreviewDocument(print, layout, null, null);

        Assert.Equal("Acme", options.Content.Company.Name);
        Assert.Equal("29AAA", options.Content.Company.Gstin);
        Assert.Equal("HDFC", options.Content.Company.BankName);
        Assert.Equal("HDFC0000123", options.Content.Company.BankIfsc);
        Assert.True(options.Content.Company.HasAnyBankField);
        Assert.Equal("T&C apply.", options.Content.Company.TermsAndConditions);
    }

    [Fact]
    public void ComposePreviewDocument_uses_provided_font_sizes()
    {
        var print = Print(printFontSize: 14, termsFontSize: 11);
        var options = PrintProfileMapping.ComposePreviewDocument(print, Layout(), null, null);

        Assert.Equal(14, options.Layout.InvoiceFontSize);
        Assert.Equal(11, options.Layout.TermsFontSize);
    }

    [Fact]
    public void ComposePreviewDocument_respects_layout_margin_and_placement()
    {
        var layout = new PrintLayoutSettings(
            LeftMarginCm: 2.0,
            RightMarginCm: 1.5,
            TopMarginCm: 0.5,
            BottomMarginCm: 1.2,
            Logo: new PrintLayoutAssetPlacement(Guid.NewGuid(), OffsetXCm: 0.3, OffsetYCm: 0.4, WidthCm: 4.0, HeightCm: 1.5),
            Signature: null);

        var options = PrintProfileMapping.ComposePreviewDocument(Print(), layout, null, null);

        Assert.Equal(20f, options.Layout.MarginLeftMm);
        Assert.Equal(15f, options.Layout.MarginRightMm);
        Assert.Equal(5f, options.Layout.MarginTopMm);
        Assert.Equal(12f, options.Layout.MarginBottomMm);
        Assert.Equal(40f, options.Layout.LogoWidthMm);
        Assert.Equal(15f, options.Layout.LogoHeightMm);
    }

    [Fact]
    public void ComposePreviewDocument_carries_asset_bytes_through()
    {
        var logo = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var sig = new byte[] { 1, 2, 3 };
        var options = PrintProfileMapping.ComposePreviewDocument(Print(), Layout(), logo, sig);

        Assert.Same(logo, options.Layout.LogoBytes);
        Assert.Same(sig, options.Layout.SignatureBytes);
    }

    [Fact]
    public void ComposePreviewDocument_always_produces_single_Original_copy()
    {
        // Saved copy defaults should not influence the preview's copy count.
        var print = Print();
        var options = PrintProfileMapping.ComposePreviewDocument(print, Layout(), null, null);

        Assert.Single(options.Copies);
        Assert.Equal(CopyLabel.Original, options.Copies[0]);
    }

    [Fact]
    public void ComposePreviewDocument_uses_the_canned_sample_for_lines()
    {
        var options = PrintProfileMapping.ComposePreviewDocument(Print(), Layout(), null, null);

        // Sample provider is the only source of line data; guard that wiring.
        Assert.Equal(2, options.Content.Lines.Count);
    }

    private static PrintSettingsDto Print(
        string companyName = "—",
        string? gstin = null,
        string? phone = null,
        string? address = null,
        string? bankName = null,
        string? bankIfsc = null,
        string? terms = null,
        int printFontSize = 11,
        int termsFontSize = 9) =>
        new(
            CompanyName: companyName,
            CompanyGstin: gstin,
            CompanyPhone: phone,
            CompanyAddress: address,
            CompanyState: null,
            CompanyCountry: null,
            BankName: bankName,
            BankAccount: null,
            BankIfsc: bankIfsc,
            BankUpi: null,
            TermsAndConditions: terms,
            CopyOriginalDefault: true,
            CopyDuplicateDefault: false,
            CopyTriplicateDefault: false,
            PrintFontSize: printFontSize,
            PrintTermsFontSize: termsFontSize);

    private static PrintLayoutSettings Layout() =>
        new(LeftMarginCm: 1.0, RightMarginCm: 1.0, TopMarginCm: 1.0, BottomMarginCm: 1.2, Logo: null, Signature: null);
}
