using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Leases;

public sealed class DraftLeaseService(ShowroomBillingDbContext dbContext) : IDraftLeaseService
{
    private const string DefaultShowroomCode = "default";
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(2);

    public async Task<DraftLeaseAcquireResult> AcquireAsync(
        DraftLeaseAcquireRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OwnerActorId))
        {
            throw new ArgumentException("OwnerActorId is required.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await LoadActiveLeaseAsync(request.BillId, cancellationToken);

        if (existing is not null)
        {
            var isExpired = existing.ExpiresAtUtc <= now;
            var isSameOwner = string.Equals(existing.OwnerActorId, request.OwnerActorId, StringComparison.Ordinal);

            if (!isExpired && !isSameOwner)
            {
                throw new DraftLeaseConflictException(
                    $"Bill '{request.BillId}' is currently locked by '{existing.OwnerActorId}'.",
                    MapResponse(existing));
            }

            if (isSameOwner && !isExpired)
            {
                existing.RenewedAtUtc = now;
                existing.ExpiresAtUtc = now.Add(LeaseTtl);
                existing.OwnerCounterName = request.OwnerCounterName ?? existing.OwnerCounterName;
                await dbContext.SaveChangesAsync(cancellationToken);
                return new DraftLeaseAcquireResult(MapResponse(existing), Reacquired: true);
            }

            existing.ReleasedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var showroomId = ResolveShowroomId(DefaultShowroomCode);
        var lease = new DraftEditLeaseEntity
        {
            Id = Guid.CreateVersion7(),
            BillId = request.BillId,
            ShowroomId = showroomId,
            OwnerActorId = request.OwnerActorId,
            OwnerCounterName = request.OwnerCounterName,
            AcquiredAtUtc = now,
            ExpiresAtUtc = now.Add(LeaseTtl),
            RenewedAtUtc = now,
            ReleasedAtUtc = null
        };

        dbContext.DraftEditLeases.Add(lease);
        WriteAudit(lease, "lease.acquired", now, request.OwnerActorId);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new DraftLeaseAcquireResult(MapResponse(lease), Reacquired: false);
    }

    public async Task<DraftLeaseResponse> RenewAsync(
        Guid leaseId,
        DraftLeaseRenewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        var lease = await dbContext.DraftEditLeases
            .FirstOrDefaultAsync(x => x.Id == leaseId && x.ReleasedAtUtc == null, cancellationToken)
            ?? throw new DraftLeaseNotFoundException(leaseId);

        if (lease.ExpiresAtUtc <= now)
        {
            throw new DraftLeaseNotFoundException(leaseId);
        }

        if (!string.Equals(lease.OwnerActorId, request.OwnerActorId, StringComparison.Ordinal))
        {
            throw new DraftLeaseOwnershipException(
                $"Lease '{leaseId}' is owned by another actor.");
        }

        lease.RenewedAtUtc = now;
        lease.ExpiresAtUtc = now.Add(LeaseTtl);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapResponse(lease);
    }

    public async Task<DraftLeaseResponse> ReleaseAsync(
        Guid leaseId,
        DraftLeaseReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = DateTimeOffset.UtcNow;
        var lease = await dbContext.DraftEditLeases
            .FirstOrDefaultAsync(x => x.Id == leaseId, cancellationToken)
            ?? throw new DraftLeaseNotFoundException(leaseId);

        if (lease.ReleasedAtUtc is not null)
        {
            return MapResponse(lease);
        }

        if (!string.Equals(lease.OwnerActorId, request.OwnerActorId, StringComparison.Ordinal))
        {
            throw new DraftLeaseOwnershipException(
                $"Lease '{leaseId}' is owned by another actor.");
        }

        lease.ReleasedAtUtc = now;
        WriteAudit(lease, "lease.released", now, request.OwnerActorId);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapResponse(lease);
    }

    public async Task<DraftLeaseResponse?> GetActiveForBillAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
    {
        var lease = await LoadActiveLeaseAsync(billId, cancellationToken);
        if (lease is null)
        {
            return null;
        }
        if (lease.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return null;
        }
        return MapResponse(lease);
    }

    public async Task<DraftLeaseListResponse> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leases = await dbContext.DraftEditLeases
            .Where(x => x.ReleasedAtUtc == null && x.ExpiresAtUtc > now)
            .OrderByDescending(x => x.AcquiredAtUtc)
            .ToListAsync(cancellationToken);

        return new DraftLeaseListResponse(leases.Select(MapResponse).ToList());
    }

    public async Task<DraftLeaseResponse> ForceReleaseAsync(
        Guid leaseId,
        DraftLeaseForceReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ArgumentException("Reason is required.", nameof(request));
        }

        var now = DateTimeOffset.UtcNow;
        var lease = await dbContext.DraftEditLeases
            .FirstOrDefaultAsync(x => x.Id == leaseId, cancellationToken)
            ?? throw new DraftLeaseNotFoundException(leaseId);

        if (lease.ReleasedAtUtc is not null)
        {
            return MapResponse(lease);
        }

        lease.ReleasedAtUtc = now;
        WriteAudit(
            lease,
            "lease.force_released",
            now,
            request.AdminActorLabel,
            extraPayloadJson: $"{{\"reason\":{System.Text.Json.JsonSerializer.Serialize(request.Reason)}}}");
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapResponse(lease);
    }

    private Task<DraftEditLeaseEntity?> LoadActiveLeaseAsync(Guid billId, CancellationToken cancellationToken)
    {
        return dbContext.DraftEditLeases
            .FirstOrDefaultAsync(x => x.BillId == billId && x.ReleasedAtUtc == null, cancellationToken);
    }

    private void WriteAudit(
        DraftEditLeaseEntity lease,
        string eventType,
        DateTimeOffset at,
        string actorId,
        string? extraPayloadJson = null)
    {
        var payload = extraPayloadJson is null
            ? $"{{\"billId\":\"{lease.BillId}\",\"leaseId\":\"{lease.Id}\"}}"
            : $"{{\"billId\":\"{lease.BillId}\",\"leaseId\":\"{lease.Id}\",\"details\":{extraPayloadJson}}}";
        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            EntityType = "draft_lease",
            EntityId = lease.Id.ToString(),
            EventType = eventType,
            ActorType = "user",
            ActorId = actorId,
            PayloadJson = payload,
            CreatedAtUtc = at
        });
    }

    private static DraftLeaseResponse MapResponse(DraftEditLeaseEntity lease) =>
        new(
            lease.Id,
            lease.BillId,
            lease.ShowroomId,
            lease.OwnerActorId,
            lease.OwnerCounterName,
            lease.AcquiredAtUtc,
            lease.ExpiresAtUtc,
            lease.RenewedAtUtc,
            lease.ReleasedAtUtc);

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }
}
