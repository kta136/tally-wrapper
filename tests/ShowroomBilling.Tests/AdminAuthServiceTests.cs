using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Infrastructure.Admin;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests;

public sealed class AdminAuthServiceTests(PostgresFixture postgres) : DatabaseTest(postgres)
{
    [Fact]
    public async Task GetPasscodeStatusAsync_ReturnsFalse_WhenNoPasscodeConfigured()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var status = await service.GetPasscodeStatusAsync();

        Assert.False(status.IsConfigured);
    }

    [Fact]
    public async Task SetPasscodeAsync_ThenStatusReportsConfigured()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "246810"));
        var status = await service.GetPasscodeStatusAsync();

        Assert.True(status.IsConfigured);
    }

    [Fact]
    public async Task SetPasscodeAsync_RejectsNewWithoutCurrent_WhenAlreadyConfigured()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "111111"));

        await Assert.ThrowsAsync<AdminPasscodeInvalidException>(() =>
            service.SetPasscodeAsync(new AdminSetPasscodeRequest("wrong", "222222")));
    }

    [Fact]
    public async Task SetPasscodeAsync_AllowsRotation_WhenCurrentIsValid()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "111111"));

        await service.SetPasscodeAsync(new AdminSetPasscodeRequest("111111", "222222"));

        await Assert.ThrowsAsync<AdminPasscodeInvalidException>(() =>
            service.UnlockAsync(new AdminUnlockRequest("111111", null)));
        var unlock = await service.UnlockAsync(new AdminUnlockRequest("222222", null));
        Assert.NotNull(unlock.Token);
    }

    [Fact]
    public async Task SetPasscodeAsync_RepeatedWrongCurrentPasscode_IsThrottledAndAudited()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "111111"));

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<AdminPasscodeInvalidException>(() =>
                service.SetPasscodeAsync(new AdminSetPasscodeRequest($"wrong{i}", "222222")));
        }

        await Assert.ThrowsAsync<AdminUnlockThrottledException>(() =>
            service.SetPasscodeAsync(new AdminSetPasscodeRequest("111111", "222222")));
        Assert.Equal(5, await db.AuditEvents.CountAsync(x => x.EventType == "admin.passcode.change.failed"));
    }

    [Fact]
    public void Throttle_CountsAttemptsBeforeVerification_SoConcurrentGuessesCannotSlipThrough()
    {
        var throttle = new AdminUnlockThrottle();
        var showroomId = Guid.NewGuid();

        var allowed = Enumerable.Range(0, 20)
            .AsParallel()
            .Count(_ => throttle.TryBeginAttempt(showroomId) == TimeSpan.Zero);

        Assert.Equal(5, allowed);
    }

    [Fact]
    public async Task UnlockAsync_RemovesSessionsThatExpiredOverThirtyDaysAgo_AndKeepsTheRest()
    {
        var now = DateTimeOffset.UtcNow;
        var longExpired = Guid.NewGuid();
        var recentlyExpired = Guid.NewGuid();
        await using var db = CreateDbContext();
        db.AdminSessions.AddRange(
            Session(longExpired, expiresAt: now.AddDays(-40)),
            Session(recentlyExpired, expiresAt: now.AddDays(-2)));
        await db.SaveChangesAsync();
        var service = CreateService(db);
        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "111111"));

        await service.UnlockAsync(new AdminUnlockRequest("111111", null));

        await using var check = CreateDbContext();
        var remaining = await check.AdminSessions.Select(x => x.Id).ToListAsync();
        Assert.DoesNotContain(longExpired, remaining);
        Assert.Contains(recentlyExpired, remaining);
        Assert.Equal(2, remaining.Count);
    }

    private static ShowroomBilling.Infrastructure.Persistence.Entities.AdminSessionEntity Session(Guid id, DateTimeOffset expiresAt) => new()
    {
        Id = id,
        ShowroomId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        ActorLabel = "test",
        IssuedAtUtc = expiresAt.AddMinutes(-30),
        ExpiresAtUtc = expiresAt,
    };

    [Fact]
    public async Task UnlockAsync_ThrowsIfNoPasscodeConfigured()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<AdminPasscodeNotConfiguredException>(() =>
            service.UnlockAsync(new AdminUnlockRequest("000000", null)));
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsSession_ForFreshToken()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "456789"));
        var unlock = await service.UnlockAsync(new AdminUnlockRequest("456789", "owner"));

        var session = await service.ValidateTokenAsync(unlock.Token);

        Assert.NotNull(session);
        Assert.Equal("owner", session!.ActorLabel);
    }

    [Fact]
    public async Task LogoutAsync_RevokesSession_SoValidateReturnsNull()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await service.SetPasscodeAsync(new AdminSetPasscodeRequest(null, "456789"));
        var unlock = await service.UnlockAsync(new AdminUnlockRequest("456789", null));

        await service.LogoutAsync(new AdminLogoutRequest(unlock.Token));

        var session = await service.ValidateTokenAsync(unlock.Token);
        Assert.Null(session);
    }

    [Fact]
    public async Task ValidateTokenAsync_ReturnsNull_ForUnknownToken()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);

        var session = await service.ValidateTokenAsync("junk-token");

        Assert.Null(session);
    }

    private static AdminAuthService CreateService(ShowroomBillingDbContext db) =>
        new(db, new AdminUnlockThrottle(), NullLogger<AdminAuthService>.Instance);
}
