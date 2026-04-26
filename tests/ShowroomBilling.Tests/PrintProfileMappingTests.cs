using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Tests;

public class PrintProfileMappingTests
{
    [Fact]
    public void ToCompanyProfile_trims_strings_and_coerces_blanks_to_null()
    {
        var dto = new PrintSettingsDto(
            CompanyName: "  Acme Jewellers  ",
            CompanyGstin: "29ABCDE1234F1Z5",
            CompanyPhone: "   ",
            CompanyAddress: "Shop 12",
            CompanyState: null,
            CompanyCountry: "India",
            BankName: "HDFC",
            BankAccount: "",
            BankIfsc: "HDFC0000123",
            BankUpi: null,
            TermsAndConditions: "Goods once sold will not be taken back.",
            CopyOriginalDefault: true,
            CopyDuplicateDefault: false,
            CopyTriplicateDefault: false,
            PrintFontSize: 11,
            PrintTermsFontSize: 9);

        var profile = PrintProfileMapping.ToCompanyProfile(dto);

        Assert.Equal("Acme Jewellers", profile.Name);
        Assert.Equal("29ABCDE1234F1Z5", profile.Gstin);
        Assert.Null(profile.Phone);
        Assert.Equal("Shop 12", profile.Address);
        Assert.Null(profile.State);
        Assert.Equal("India", profile.Country);
        Assert.Equal("HDFC", profile.BankName);
        Assert.Null(profile.BankAccount);
        Assert.Equal("HDFC0000123", profile.BankIfsc);
        Assert.Null(profile.BankUpi);
        Assert.NotNull(profile.TermsAndConditions);
    }

    [Fact]
    public void ToCompanyProfile_emdash_name_when_blank()
    {
        var dto = new PrintSettingsDto(
            CompanyName: "",
            CompanyGstin: null,
            CompanyPhone: null,
            CompanyAddress: null,
            CompanyState: null,
            CompanyCountry: null,
            BankName: null,
            BankAccount: null,
            BankIfsc: null,
            BankUpi: null,
            TermsAndConditions: null,
            CopyOriginalDefault: true,
            CopyDuplicateDefault: false,
            CopyTriplicateDefault: false,
            PrintFontSize: 11,
            PrintTermsFontSize: 9);

        var profile = PrintProfileMapping.ToCompanyProfile(dto);

        Assert.Equal("—", profile.Name);
        Assert.Equal(string.Empty, profile.Gstin);
        Assert.False(profile.HasAnyBankField);
    }

    [Fact]
    public void HasAnyBankField_is_true_when_any_field_set()
    {
        var profile = new CompanyProfile(
            Name: "X", Address: null, Phone: null, State: null, Country: null,
            Gstin: "", Huid: null,
            BankName: null, BankAccount: null, BankIfsc: "HDFC0", BankUpi: null,
            TermsAndConditions: null);
        Assert.True(profile.HasAnyBankField);
    }

    [Fact]
    public void ToPrintLayoutOptions_converts_cm_to_mm_and_clamps()
    {
        var settings = new PrintLayoutSettings(
            LeftMarginCm: 1.0,
            RightMarginCm: 1.5,
            TopMarginCm: 0.0,
            BottomMarginCm: 99.0, // will clamp to 25mm
            Logo: new PrintLayoutAssetPlacement(AssetId: null, OffsetXCm: 1.0, OffsetYCm: 0.5, WidthCm: 4.0, HeightCm: 1.5),
            Signature: null);

        var options = PrintProfileMapping.ToPrintLayoutOptions(settings, logoBytes: null, signatureBytes: null, invoiceFontSize: 11, termsFontSize: 9);

        Assert.Equal(10f, options.MarginLeftMm);
        Assert.Equal(15f, options.MarginRightMm);
        Assert.Equal(0f, options.MarginTopMm);
        Assert.Equal(25f, options.MarginBottomMm); // clamped
        Assert.Equal(40f, options.LogoWidthMm);
        Assert.Equal(15f, options.LogoHeightMm);
    }

    [Fact]
    public void ToPrintLayoutOptions_falls_back_to_defaults_when_no_placement()
    {
        var settings = new PrintLayoutSettings(
            LeftMarginCm: 1.0, RightMarginCm: 1.0, TopMarginCm: 1.0, BottomMarginCm: 1.2,
            Logo: null, Signature: null);

        var options = PrintProfileMapping.ToPrintLayoutOptions(settings, null, null, 11, 9);

        Assert.True(options.LogoWidthMm > 0f);
        Assert.True(options.SignatureWidthMm > 0f);
    }

    [Fact]
    public void ToPrintLayoutOptions_passes_asset_bytes_through()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var settings = new PrintLayoutSettings(1.0, 1.0, 1.0, 1.2, null, null);

        var options = PrintProfileMapping.ToPrintLayoutOptions(settings, bytes, bytes, 11, 9);

        Assert.Same(bytes, options.LogoBytes);
        Assert.Same(bytes, options.SignatureBytes);
    }
}
