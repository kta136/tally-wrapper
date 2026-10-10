using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Leases;

/// <summary>
/// The admin side of draft edit leases. Nothing takes a lease (acquire, renew
/// and release were removed because no client ever called them), so the list
/// is empty unless a row was put in <c>draft_edit_leases</c> some other way.
/// </summary>
public sealed class DraftLeaseService(ShowroomBillingDbContext dbContext) : IDraftLeaseService
{
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
        dbContext.AuditEvents.Add(AuditRow.Create(
            "draft_lease",
            lease.Id.ToString(),
            "lease.force_released",
            new AuditActor("user", request.AdminActorLabel),
            now,
            new { billId = lease.BillId, leaseId = lease.Id, details = new { reason = request.Reason } }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapResponse(lease);
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
