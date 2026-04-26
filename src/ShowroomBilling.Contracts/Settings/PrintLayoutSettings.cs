namespace ShowroomBilling.Contracts.Settings;

public sealed record PrintLayoutSettings(
    double LeftMarginCm,
    double RightMarginCm,
    double TopMarginCm,
    double BottomMarginCm,
    PrintLayoutAssetPlacement? Logo,
    PrintLayoutAssetPlacement? Signature);

public sealed record PrintLayoutAssetPlacement(
    Guid? AssetId,
    double OffsetXCm,
    double OffsetYCm,
    double WidthCm,
    double HeightCm);

public sealed record PrintLayoutResponse(
    PrintLayoutSettings Layout,
    DateTimeOffset UpdatedAtUtc);

public sealed record UpdatePrintLayoutRequest(PrintLayoutSettings Layout);
