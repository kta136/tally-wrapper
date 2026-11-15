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
    string AssetKind,
    string FileName,
    string ContentType,
    long ByteLength,
    DateTimeOffset CreatedAtUtc);

public sealed record PrintAssetListResponse(IReadOnlyList<PrintAssetResponse> Assets);

/// <summary>An image to store. Its type is read from the bytes themselves, so none is sent.</summary>
public sealed record PrintAssetUploadRequest(
    string AssetKind,
    string FileName,
    string Base64Content);
