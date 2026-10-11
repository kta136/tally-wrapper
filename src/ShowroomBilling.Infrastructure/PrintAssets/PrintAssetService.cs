using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.PrintAssets;

public sealed class PrintAssetService(
    ShowroomBillingDbContext dbContext,
    IAuditActorContext? actorContext = null)
{
    private const long MaxBytes = 2 * 1024 * 1024;

    // Base64 writes four characters for every three bytes, rounded up.
    private const int MaxBase64Characters = (int)((MaxBytes + 2) / 3 * 4);

    public async Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default)
    {
        var showroomId = Showrooms.DefaultId;
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
            throw new RequestValidationException(
                $"AssetKind must be one of: {string.Join(", ", PrintAssetKinds.All)}.");
        }
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new RequestValidationException("FileName is required.");
        }
        var fileName = request.FileName.Trim();
        // Both separators are named: Path.GetFileName only knows the ones of
        // the system it runs on, and the name ends up on Windows PCs.
        if (fileName.Length > 256
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new RequestValidationException("FileName must be a plain file name of at most 256 characters.");
        }
        // request.ContentType is not read: the type is taken from the bytes themselves.
        if (string.IsNullOrWhiteSpace(request.Base64Content))
        {
            throw new RequestValidationException("Base64Content is required.");
        }
        if (request.Base64Content.Length > MaxBase64Characters)
        {
            throw new RequestValidationException($"Asset exceeds maximum size of {MaxBytes / 1024} KB.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Base64Content);
        }
        catch (FormatException)
        {
            throw new RequestValidationException("Base64Content is not valid base64.");
        }

        if (bytes.Length == 0)
        {
            throw new RequestValidationException("Base64Content decoded to an empty byte array.");
        }
        if (bytes.Length > MaxBytes)
        {
            throw new RequestValidationException($"Asset exceeds maximum size of {MaxBytes / 1024} KB.");
        }

        var contentType = ImageSignature.DetectContentType(bytes)
            ?? throw new RequestValidationException("Asset must be a valid PNG or JPEG image.");

        var showroomId = Showrooms.DefaultId;
        var now = DateTimeOffset.UtcNow;
        var entity = new PrintAssetEntity
        {
            Id = Guid.CreateVersion7(),
            ShowroomId = showroomId,
            AssetKind = request.AssetKind,
            FileName = fileName,
            ContentType = contentType,
            ByteLength = bytes.LongLength,
            Bytes = bytes,
            CreatedAtUtc = now
        };
        dbContext.PrintAssets.Add(entity);
        var actor = actorContext?.Current ?? AuditRow.System;
        dbContext.AuditEvents.Add(AuditRow.Create(
            "print_asset", entity.Id.ToString(), "print_asset.uploaded", actor, now,
            new { kind = entity.AssetKind, size = entity.ByteLength }));
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(entity);
    }

    public async Task<(PrintAssetResponse Metadata, byte[] Bytes)?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PrintAssets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : (ToResponse(entity), entity.Bytes);
    }

    private static PrintAssetResponse ToResponse(PrintAssetEntity entity) => new(
        entity.Id,
        entity.ShowroomId,
        entity.AssetKind,
        entity.FileName,
        entity.ContentType,
        entity.ByteLength,
        entity.CreatedAtUtc);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.PrintAssets.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null)
        {
            return false;
        }
        dbContext.PrintAssets.Remove(entity);
        var actor = actorContext?.Current ?? AuditRow.System;
        dbContext.AuditEvents.Add(AuditRow.Create(
            "print_asset", id.ToString(), "print_asset.deleted", actor, DateTimeOffset.UtcNow,
            new { kind = entity.AssetKind }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
