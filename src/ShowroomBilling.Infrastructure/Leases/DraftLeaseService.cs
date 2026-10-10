using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Leases;

public sealed class DraftLeaseService(ShowroomBillingDbContext dbContext) : IDraftLeaseService
{
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(2);

    public async Task<DraftLeaseAcquireResult> AcquireAsync(
        DraftLeaseAcquireRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.OwnerActorId))
        {
            throw new RequestValidationException("OwnerActorId is required.");
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

        var showroomId = Showrooms.DefaultId;
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
        var now = DateTimeOffset.UtcNow;
        var lease = await dbContext.DraftEditLeases
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.BillId == billId && x.ReleasedAtUtc == null && x.ExpiresAtUtc > now,
                cancellationToken);
        return lease is null ? null : MapResponse(lease);
    }

    public async Task<DraftLeaseListResponse> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leases = await dbContext.DraftEditLeases
            .AsNoTracking()
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
            throw new RequestValidationException("Reason is required.");
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
            details: new { reason = request.Reason });
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
        object? details = null)
    {
        object payload = details is null
            ? new { billId = lease.BillId, leaseId = lease.Id }
            : new { billId = lease.BillId, leaseId = lease.Id, details };
        dbContext.AuditEvents.Add(AuditRow.Create(
            "draft_lease", lease.Id.ToString(), eventType, new AuditActor("user", actorId), at, payload));
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

}
