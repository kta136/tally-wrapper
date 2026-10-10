using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Tests;

public sealed class OperationalDataRetentionHostedServiceTests
{
    [Fact]
    public async Task Purge_RemovesOnlyOperationalRowsOlderThanRetentionWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var staleSessionId = Guid.NewGuid();
        var currentSessionId = Guid.NewGuid();
        var databaseName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddDbContext<ShowroomBillingDbContext>(options => options.UseInMemoryDatabase(databaseName));
        await using var provider = services.BuildServiceProvider();

        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
            db.AdminSessions.AddRange(
                Session(staleSessionId, now.AddDays(-45), now.AddDays(-40)),
                Session(currentSessionId, now.AddDays(-2), now.AddDays(1)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var startupStatus = new StartupStatus();
        var service = new OperationalDataRetentionHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            startupStatus,
            NullLogger<OperationalDataRetentionHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        startupStatus.RecordDatabaseReady();

        await WaitForAsync(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
            return await db.AdminSessions.CountAsync(TestContext.Current.CancellationToken) == 1;
        });

        await service.StopAsync(TestContext.Current.CancellationToken);

        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
        Assert.False(await assertDb.AdminSessions.AnyAsync(x => x.Id == staleSessionId, TestContext.Current.CancellationToken));
        Assert.True(await assertDb.AdminSessions.AnyAsync(x => x.Id == currentSessionId, TestContext.Current.CancellationToken));
    }

    private static AdminSessionEntity Session(Guid id, DateTimeOffset issuedAt, DateTimeOffset expiresAt) => new()
    {
        Id = id,
        ShowroomId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString("N"),
        ActorLabel = "test",
        IssuedAtUtc = issuedAt,
        ExpiresAtUtc = expiresAt,
    };

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Operational retention did not finish within two seconds.");
    }
}
