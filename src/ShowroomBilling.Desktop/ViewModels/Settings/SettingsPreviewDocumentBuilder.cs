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
        byte[]? serverSignatureBytes)
    {
        var print = draft.BuildPrintSettingsSnapshot();
        var layout = SnapshotPrintLayoutSettings(printLayout);
        var logoBytes = printLayout.PendingLogoBytes ?? serverLogoBytes;
        var signatureBytes = printLayout.PendingSignatureBytes ?? serverSignatureBytes;
        return PrintProfileMapping.ComposePreviewDocument(print, layout, logoBytes, signatureBytes);
    }

    private static PrintLayoutSettings SnapshotPrintLayoutSettings(PrintLayoutViewModel vm)
        => new(
            LeftMarginCm: vm.LeftMarginCm,
            RightMarginCm: vm.RightMarginCm,
            TopMarginCm: vm.TopMarginCm,
            BottomMarginCm: vm.BottomMarginCm,
            Logo: vm.LogoAssetId is null
                ? null
                : new PrintLayoutAssetPlacement(vm.LogoAssetId, vm.LogoOffsetXCm, vm.LogoOffsetYCm, vm.LogoWidthCm, vm.LogoHeightCm),
            Signature: vm.SignatureAssetId is null
                ? null
                : new PrintLayoutAssetPlacement(vm.SignatureAssetId, vm.SignatureOffsetXCm, vm.SignatureOffsetYCm, vm.SignatureWidthCm, vm.SignatureHeightCm));
}
