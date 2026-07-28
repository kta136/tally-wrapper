namespace ShowroomBilling.Contracts.PrintAssets;

public static class PrintAssetKinds
{
    public const string Logo = "logo";
    public const string Signature = "signature";
    public const string Watermark = "watermark";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Logo,
        Signature,
        Watermark
    };
}

public sealed record PrintAssetResponse(
    Guid Id,
    Guid ShowroomId,
    string AssetKind,
    string FileName,
    string ContentType,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);

public sealed record PrintAssetListResponse(IReadOnlyList<PrintAssetResponse> Assets);

public sealed record PrintAssetUploadRequest(
    string AssetKind,
    string FileName,
    string ContentType,
    string Base64Content);
