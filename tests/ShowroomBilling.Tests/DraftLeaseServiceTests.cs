using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;
using ShowroomBilling.Infrastructure.Leases;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Tests;

public sealed class DraftLeaseServiceTests
{
    [Fact]
    public async Task AcquireAsync_CreatesNewLease_WhenBillHasNoActiveLease()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);
        var billId = Guid.NewGuid();

        var result = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-1", "Counter 1"));

        Assert.False(result.Reacquired);
        Assert.Equal(billId, result.Lease.BillId);
        Assert.Equal("counter-1", result.Lease.OwnerActorId);
        Assert.Null(result.Lease.ReleasedAtUtc);
        Assert.True(result.Lease.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AcquireAsync_ThrowsConflict_WhenDifferentActorHoldsLease()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);
        var billId = Guid.NewGuid();
        await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-a", null));

        var ex = await Assert.ThrowsAsync<DraftLeaseConflictException>(() =>
            service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-b", null)));

        Assert.Equal("counter-a", ex.ExistingLease.OwnerActorId);
    }

    [Fact]
    public async Task AcquireAsync_RenewsExistingLease_WhenSameActorReacquires()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);
        var billId = Guid.NewGuid();
        var first = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-a", null));

        var second = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-a", null));

        Assert.True(second.Reacquired);
        Assert.Equal(first.Lease.Id, second.Lease.Id);
        Assert.True(second.Lease.RenewedAtUtc >= first.Lease.RenewedAtUtc);
    }

    [Fact]
    public async Task AcquireAsync_TakesOverLease_WhenExistingLeaseIsExpired()
    {
        await using var db = CreateDbContext();
        var showroomId = Guid.Parse("f4d7a0f4-0b8f-9b6d-d1d0-4a5b5b9c2d3e");
        var billId = Guid.NewGuid();
        db.DraftEditLeases.Add(new DraftEditLeaseEntity
        {
            Id = Guid.NewGuid(),
            BillId = billId,
            ShowroomId = showroomId,
            OwnerActorId = "counter-a",
            AcquiredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            RenewedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();
        var service = new DraftLeaseService(db);

        var result = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-b", null));

        Assert.False(result.Reacquired);
        Assert.Equal("counter-b", result.Lease.OwnerActorId);
    }

    [Fact]
    public async Task RenewAsync_RejectsWrongOwner()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);
        var billId = Guid.NewGuid();
        var acquired = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-a", null));

        await Assert.ThrowsAsync<DraftLeaseOwnershipException>(() =>
            service.RenewAsync(acquired.Lease.Id, new DraftLeaseRenewRequest("counter-b")));
    }

    [Fact]
    public async Task ReleaseAsync_MarksLeaseReleased()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);
        var billId = Guid.NewGuid();
        var acquired = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-a", null));

        var released = await service.ReleaseAsync(acquired.Lease.Id, new DraftLeaseReleaseRequest("counter-a"));

        Assert.NotNull(released.ReleasedAtUtc);
        var active = await service.GetActiveForBillAsync(billId);
        Assert.Null(active);
    }

    [Fact]
    public async Task ForceReleaseAsync_ReleasesEvenWithDifferentActor()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);
        var billId = Guid.NewGuid();
        var acquired = await service.AcquireAsync(new DraftLeaseAcquireRequest(billId, "counter-a", null));

        var released = await service.ForceReleaseAsync(
            acquired.Lease.Id,
            new DraftLeaseForceReleaseRequest("admin", "kiosk crashed"));

        Assert.NotNull(released.ReleasedAtUtc);
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesReleasedAndExpired()
    {
        await using var db = CreateDbContext();
        var service = new DraftLeaseService(db);

        var billA = Guid.NewGuid();
        var billB = Guid.NewGuid();
        var billC = Guid.NewGuid();
        await service.AcquireAsync(new DraftLeaseAcquireRequest(billA, "counter-a", null));
        var b = await service.AcquireAsync(new DraftLeaseAcquireRequest(billB, "counter-b", null));
        await service.ReleaseAsync(b.Lease.Id, new DraftLeaseReleaseRequest("counter-b"));
        db.DraftEditLeases.Add(new DraftEditLeaseEntity
        {
            Id = Guid.NewGuid(),
            BillId = billC,
            ShowroomId = Guid.NewGuid(),
            OwnerActorId = "counter-c",
            AcquiredAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            RenewedAtUtc = DateTimeOffset.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        var list = await service.ListActiveAsync();

        Assert.Single(list.Leases);
        Assert.Equal(billA, list.Leases[0].BillId);
    }

    private static ShowroomBillingDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ShowroomBillingDbContext(options);
    }
}
