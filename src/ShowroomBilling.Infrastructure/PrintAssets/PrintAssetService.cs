using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.PrintAssets;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.PrintAssets;

public sealed class PrintAssetService(
    ShowroomBillingDbContext dbContext,
    IAuditActorContext? actorContext = null) : IPrintAssetService
{
    private const string DefaultShowroomCode = "default";
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxBase64Characters = 2_800_000;

    public async Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var rows = await dbContext.PrintAssets
            .AsNoTracking()
            .Where(x => x.ShowroomId == showroomId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new PrintAssetResponse(
                x.Id,
                x.ShowroomId,
                x.AssetKind,
                x.FileName,
                x.ContentType,
                x.ByteLength,
                x.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return new PrintAssetListResponse(rows);
    }

    public async Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PrintAssetKinds.All.Contains(request.AssetKind))
        {
            throw new ArgumentException(
                $"AssetKind must be one of: {string.Join(", ", PrintAssetKinds.All)}.",
                nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("FileName is required.", nameof(request));
        }
        var fileName = request.FileName.Trim();
        if (fileName.Length > 256 || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("FileName must be a plain file name of at most 256 characters.", nameof(request));
        }
        if (request.ContentType?.Length > 128)
        {
            throw new ArgumentException("ContentType cannot exceed 128 characters.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Base64Content))
        {
            throw new ArgumentException("Base64Content is required.", nameof(request));
        }
        if (request.Base64Content.Length > MaxBase64Characters)
        {
            throw new ArgumentException($"Asset exceeds maximum size of {MaxBytes / 1024} KB.", nameof(request));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Base64Content);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Base64Content is not valid base64.", nameof(request));
        }

        if (bytes.Length == 0)
        {
            throw new ArgumentException("Base64Content decoded to an empty byte array.", nameof(request));
        }
        if (bytes.Length > MaxBytes)
        {
            throw new ArgumentException($"Asset exceeds maximum size of {MaxBytes / 1024} KB.", nameof(request));
        }

        var contentType = DetectImageContentType(bytes)
            ?? throw new ArgumentException("Asset must be a valid PNG or JPEG image.", nameof(request));

        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var now = DateTimeOffset.UtcNow;
        var entity = new PrintAssetEntity
        {
            Id = Guid.NewGuid(),
            ShowroomId = showroomId,
            AssetKind = request.AssetKind,
            FileName = fileName,
            ContentType = contentType,
            ByteLength = bytes.LongLength,
            Bytes = bytes,
            CreatedAtUtc = now
        };
        dbContext.PrintAssets.Add(entity);
        var actor = actorContext?.Current ?? new AuditActor("system", null);
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "print_asset",
            EntityId = entity.Id.ToString(),
            EventType = "print_asset.uploaded",
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            PayloadJson = JsonSerializer.Serialize(new { kind = entity.AssetKind, size = entity.ByteLength }),
            CreatedAtUtc = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PrintAssetResponse(
            entity.Id,
            entity.ShowroomId,
            entity.AssetKind,
            entity.FileName,
            entity.ContentType,
            entity.ByteLength,
            entity.CreatedAtUtc);
    }

    public async Task<(PrintAssetResponse Metadata, byte[] Bytes)?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PrintAssets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }
        var metadata = new PrintAssetResponse(
            entity.Id,
            entity.ShowroomId,
            entity.AssetKind,
            entity.FileName,
            entity.ContentType,
            entity.ByteLength,
            entity.CreatedAtUtc);
        return (metadata, entity.Bytes);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PrintAssets.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }
        dbContext.PrintAssets.Remove(entity);
        var actor = actorContext?.Current ?? new AuditActor("system", null);
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "print_asset",
            EntityId = id.ToString(),
            EventType = "print_asset.deleted",
            ActorType = actor.ActorType,
            ActorId = actor.ActorId,
            PayloadJson = JsonSerializer.Serialize(new { kind = entity.AssetKind }),
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    private static string? DetectImageContentType(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.StartsWith(png)) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        return null;
    }
}
