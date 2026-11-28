using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal static class SettingsPreviewDocumentBuilder
{
    internal static bool IsPrintAffectingProperty(string? name) => name switch
    {
        nameof(SettingsDraft.PrintCompanyName) or
        nameof(SettingsDraft.CompanyGstin) or
        nameof(SettingsDraft.CompanyPhone) or
        nameof(SettingsDraft.CompanyAddress) or
        nameof(SettingsDraft.CompanyState) or
        nameof(SettingsDraft.CompanyCountry) or
        nameof(SettingsDraft.BankName) or
        nameof(SettingsDraft.BankAccount) or
        nameof(SettingsDraft.BankIfsc) or
        nameof(SettingsDraft.BankUpi) or
        nameof(SettingsDraft.TermsAndConditions) or
        nameof(SettingsDraft.PrintFontSize) or
        nameof(SettingsDraft.PrintTermsFontSize) => true,
        _ => false,
    };

    internal static PrintDocumentOptions BuildOptions(
        SettingsDraft draft,
        PrintLayoutViewModel printLayout,
        byte[]? serverLogoBytes,
        byte[]? serverSignatureBytes,
        byte[]? serverWatermarkBytes)
    {
        var print = draft.BuildPrintSettingsSnapshot();
        var layout = printLayout.BuildLayout(preview: true);
        var logoBytes = printLayout.Logo.PendingBytes ?? serverLogoBytes;
        var signatureBytes = printLayout.Signature.PendingBytes ?? serverSignatureBytes;
        var watermarkBytes = printLayout.Watermark.PendingBytes ?? serverWatermarkBytes;
        return PrintProfileMapping.ComposePreviewDocument(print, layout, logoBytes, signatureBytes, watermarkBytes);
    }
}
