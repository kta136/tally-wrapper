using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.PrintAssets;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.PrintAssets;

public sealed class PrintAssetService(ShowroomBillingDbContext dbContext) : IPrintAssetService
{
    private const string DefaultShowroomCode = "default";
    private const long MaxBytes = 2 * 1024 * 1024;

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

        if (request.AssetKind is not PrintAssetKinds.Logo and not PrintAssetKinds.Signature)
        {
            throw new ArgumentException(
                $"AssetKind must be '{PrintAssetKinds.Logo}' or '{PrintAssetKinds.Signature}'.",
                nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("FileName is required.", nameof(request));
        }
        if (string.IsNullOrWhiteSpace(request.Base64Content))
        {
            throw new ArgumentException("Base64Content is required.", nameof(request));
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

        var contentType = string.IsNullOrWhiteSpace(request.ContentType)
            ? "application/octet-stream"
            : request.ContentType.Trim();

        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var now = DateTimeOffset.UtcNow;
        var entity = new PrintAssetEntity
        {
            Id = Guid.NewGuid(),
            ShowroomId = showroomId,
            AssetKind = request.AssetKind,
            FileName = request.FileName,
            ContentType = contentType,
            ByteLength = bytes.LongLength,
            Bytes = bytes,
            CreatedAtUtc = now
        };
        dbContext.PrintAssets.Add(entity);
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "print_asset",
            EntityId = entity.Id.ToString(),
            EventType = "print_asset.uploaded",
            ActorType = "system",
            PayloadJson = $"{{\"kind\":\"{entity.AssetKind}\",\"size\":{entity.ByteLength}}}",
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
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "print_asset",
            EntityId = id.ToString(),
            EventType = "print_asset.deleted",
            ActorType = "system",
            PayloadJson = $"{{\"kind\":\"{entity.AssetKind}\"}}",
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
}
