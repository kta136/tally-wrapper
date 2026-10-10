using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Infrastructure.Leases;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Tests;

/// <summary>
/// Only the admin side of leases is left (list, force-release). Nothing in
/// the app takes a lease, so the tests put the rows there themselves.
/// </summary>
public sealed class DraftLeaseServiceTests
{
    [Fact]
    public async Task ForceReleaseAsync_ReleasesTheLeaseAndAuditsTheReason()
    {
        await using var db = CreateDbContext();
        var lease = await SeedAsync(db, "counter-a", expiresIn: TimeSpan.FromMinutes(2));
        var service = new DraftLeaseService(db);

        var released = await service.ForceReleaseAsync(
            lease.Id,
            new DraftLeaseForceReleaseRequest("admin", "kiosk \"A\" crashed"));

        Assert.NotNull(released.ReleasedAtUtc);
        var audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("lease.force_released", audit.EventType);
        Assert.Equal("admin", audit.ActorId);
        using var payload = System.Text.Json.JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal(lease.BillId, payload.RootElement.GetProperty("billId").GetGuid());
        Assert.Equal(
            "kiosk \"A\" crashed",
            payload.RootElement.GetProperty("details").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ForceReleaseAsync_OnAReleasedLease_ChangesNothing()
    {
        await using var db = CreateDbContext();
        var releasedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var lease = await SeedAsync(db, "counter-a", expiresIn: TimeSpan.FromMinutes(2), releasedAtUtc: releasedAt);
        var service = new DraftLeaseService(db);

        var result = await service.ForceReleaseAsync(lease.Id, new DraftLeaseForceReleaseRequest("admin", "again"));

        Assert.Equal(releasedAt, result.ReleasedAtUtc);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task ForceReleaseAsync_NeedsAReason()
    {
        await using var db = CreateDbContext();
        var lease = await SeedAsync(db, "counter-a", expiresIn: TimeSpan.FromMinutes(2));
        var service = new DraftLeaseService(db);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.ForceReleaseAsync(lease.Id, new DraftLeaseForceReleaseRequest("admin", "  ")));

        Assert.Null((await db.DraftEditLeases.SingleAsync()).ReleasedAtUtc);
    }

    [Fact]
    public async Task ForceReleaseAsync_UnknownLease_IsNotFound()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);

        await Assert.ThrowsAsync<DraftLeaseNotFoundException>(() =>
            service.ForceReleaseAsync(Guid.NewGuid(), new DraftLeaseForceReleaseRequest("admin", "gone")));
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesReleasedAndExpired()
    {
        await using var db = CreateDbContext();
        var live = await SeedAsync(db, "counter-a", expiresIn: TimeSpan.FromMinutes(2));
        await SeedAsync(db, "counter-b", expiresIn: TimeSpan.FromMinutes(2), releasedAtUtc: DateTimeOffset.UtcNow);
        await SeedAsync(db, "counter-c", expiresIn: TimeSpan.FromMinutes(-10));
        var service = new DraftLeaseService(db);

        var list = await service.ListActiveAsync();

        var only = Assert.Single(list.Leases);
        Assert.Equal(live.BillId, only.BillId);
        Assert.Equal("counter-a", only.OwnerActorId);
    }

    private static async Task<DraftEditLeaseEntity> SeedAsync(
        ShowroomBillingDbContext db,
        string owner,
        TimeSpan expiresIn,
        DateTimeOffset? releasedAtUtc = null)
    {
        var now = DateTimeOffset.UtcNow;
        var lease = new DraftEditLeaseEntity
        {
            Id = Guid.NewGuid(),
            BillId = Guid.NewGuid(),
            ShowroomId = Guid.NewGuid(),
            OwnerActorId = owner,
            AcquiredAtUtc = now.AddMinutes(-15),
            ExpiresAtUtc = now.Add(expiresIn),
            RenewedAtUtc = now.AddMinutes(-15),
            ReleasedAtUtc = releasedAtUtc
        };
        db.DraftEditLeases.Add(lease);
        await db.SaveChangesAsync();
        return lease;
    }

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }
}
