namespace ShowroomBilling.Infrastructure.Persistence.Entities;

public sealed class PrintAssetEntity
{
    public Guid Id { get; set; }

    public Guid ShowroomId { get; set; }

    public string AssetKind { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public byte[] Bytes { get; set; } = Array.Empty<byte>();

    public DateTimeOffset CreatedAtUtc { get; set; }
}
