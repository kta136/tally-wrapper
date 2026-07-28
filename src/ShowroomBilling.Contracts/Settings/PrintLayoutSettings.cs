namespace ShowroomBilling.Contracts.Settings;

public sealed record PrintLayoutSettings(
    double LeftMarginCm,
    double RightMarginCm,
    double TopMarginCm,
    double BottomMarginCm,
    PrintLayoutAssetPlacement? Logo,
    PrintLayoutAssetPlacement? Signature,
    PrintLayoutWatermarkPlacement? Watermark = null,
    PrintPageLayoutSettings? PageLayout = null);

public sealed record PrintLayoutAssetPlacement(
    Guid? AssetId,
    double OffsetXCm,
    double OffsetYCm,
    double WidthCm,
    double HeightCm);

public sealed record PrintLayoutWatermarkPlacement(
    Guid AssetId,
    double OffsetXCm,
    double OffsetYCm,
    double WidthCm,
    double HeightCm,
    double OpacityPercent);

public sealed record PrintPageLayoutSettings(
    string Density,
    double InvoiceBorderThicknessPt,
    string? BottomPinnedFromSectionKey,
    IReadOnlyList<PrintLayoutSectionSettings> Sections);

public sealed record PrintLayoutSectionSettings(
    string SectionKey,
    bool IsVisible,
    double SpacingBeforeMm,
    double SpacingAfterMm);

public static class PrintPageDensity
{
    public const string Compact = "compact";
    public const string Standard = "standard";
    public const string Comfortable = "comfortable";

    public static IReadOnlyList<string> All { get; } =
        [Compact, Standard, Comfortable];
}

public static class PrintLayoutSectionKeys
{
    public const string CopyLabel = "copyLabel";
    public const string Logo = "logo";
    public const string InvoiceTitle = "invoiceTitle";
    public const string CompanyAndParty = "companyAndParty";
    public const string Notes = "notes";
    public const string ItemsTable = "itemsTable";
    public const string Totals = "totals";
    public const string GstBreakup = "gstBreakup";
    public const string BankDetails = "bankDetails";
    public const string Terms = "terms";
    public const string Signature = "signature";

    public static IReadOnlyList<string> All { get; } =
    [
        CopyLabel,
        Logo,
        InvoiceTitle,
        CompanyAndParty,
        Notes,
        ItemsTable,
        Totals,
        GstBreakup,
        BankDetails,
        Terms,
        Signature
    ];

    public static IReadOnlySet<string> Mandatory { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        CopyLabel,
        InvoiceTitle,
        CompanyAndParty,
        ItemsTable,
        Totals,
        GstBreakup
    };

    public static IReadOnlySet<string> Optional { get; } = new HashSet<string>(
        All.Where(key => !Mandatory.Contains(key)),
        StringComparer.Ordinal);
}

public static class PrintLayoutDefaults
{
    public const double A4WidthCm = 21.0;
    public const double A4HeightCm = 29.7;
    public const double WatermarkWidthCm = 12.0;
    public const double WatermarkHeightCm = 12.0;
    public const double WatermarkOffsetXCm = 4.5;
    public const double WatermarkOffsetYCm = 8.85;
    public const double WatermarkOpacityPercent = 15.0;
    public const double InvoiceBorderThicknessPt = 1.0;

    public static PrintPageLayoutSettings CreatePageLayout() =>
        new(
            Density: PrintPageDensity.Standard,
            InvoiceBorderThicknessPt: InvoiceBorderThicknessPt,
            BottomPinnedFromSectionKey: PrintLayoutSectionKeys.GstBreakup,
            Sections: PrintLayoutSectionKeys.All
                .Select(key => new PrintLayoutSectionSettings(
                    SectionKey: key,
                    IsVisible: true,
                    SpacingBeforeMm: 0,
                    SpacingAfterMm: 0))
                .ToArray());

    public static PrintLayoutWatermarkPlacement CreateWatermark(Guid assetId) =>
        new(
            AssetId: assetId,
            OffsetXCm: WatermarkOffsetXCm,
            OffsetYCm: WatermarkOffsetYCm,
            WidthCm: WatermarkWidthCm,
            HeightCm: WatermarkHeightCm,
            OpacityPercent: WatermarkOpacityPercent);
}

public sealed record PrintLayoutResponse(
    PrintLayoutSettings Layout,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdatePrintLayoutRequest(PrintLayoutSettings Layout);
