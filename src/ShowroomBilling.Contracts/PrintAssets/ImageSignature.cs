namespace ShowroomBilling.Contracts.PrintAssets;

/// <summary>
/// Tells a PNG from a JPEG by its first bytes. Logo, signature and watermark
/// uploads are accepted, and later drawn, on what the bytes are, never on a
/// file name or a declared content type.
/// </summary>
public static class ImageSignature
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    /// <summary>Returns <see cref="Png"/>, <see cref="Jpeg"/>, or null for anything else.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.StartsWith(png)) return Png;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return Jpeg;
        return null;
    }
}
