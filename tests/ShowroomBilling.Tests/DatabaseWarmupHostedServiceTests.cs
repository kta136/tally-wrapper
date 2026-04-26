using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests;

/// <summary>
/// Contract tests for <see cref="DatabaseWarmupHostedService"/>.
///
/// The warmup service exists so the FIRST user-facing request after API boot
/// doesn't pay the Neon TLS handshake + EF model-build + bills index plan
/// costs. The contract it must uphold:
///
///   1. StartAsync is non-blocking (must not wait on the warmup queries).
///   2. Warmup runs only AFTER IStartupStatus.RecordDatabaseReady fires
///      (otherwise it'd hit an unmigrated schema).
///   3. A faulted database-ready signal does NOT throw out of the service —
///      warmup is best-effort; the API stays online.
///
/// These tests exercise the contract with a real InMemory DbContext + the
/// real StartupStatus; the only thing mocked is timing.
/// </summary>
public sealed class DatabaseWarmupHostedServiceTests
{
    [Fact]
    public async Task StartAsync_ReturnsImmediately_EvenIfDatabaseNotReady()
    {
        var startupStatus = new StartupStatus(); // never signalled ready in this test
        await using var provider = BuildProvider();
        var service = new DatabaseWarmupHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            startupStatus,
            NullLogger<DatabaseWarmupHostedService>.Instance);

        var stopwatch = Stopwatch.StartNew();
        await service.StartAsync(CancellationToken.None);
        stopwatch.Stop();

        // StartAsync must not block on WaitForDatabaseReadyAsync. Generous
        // ceiling; the typical observed value is <10 ms.
        Assert.True(stopwatch.ElapsedMilliseconds < 250,
            $"StartAsync blocked for {stopwatch.ElapsedMilliseconds} ms; expected non-blocking dispatch.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Warmup_ExecutesQueries_AfterDatabaseReady()
    {
        var startupStatus = new StartupStatus();
        await using var provider = BuildProvider(seed: db =>
        {
            db.Bills.Add(new ShowroomBilling.Infrastructure.Persistence.Entities.BillEntity
            {
                Id = Guid.NewGuid(),
                ShowroomId = Guid.NewGuid(),
                FiscalYear = "2026-27",
                InvoiceNumber = "2026-27/0001",
                State = "pending",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        });
        var scopeFactory = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var service = new DatabaseWarmupHostedService(
            scopeFactory,
            startupStatus,
            NullLogger<DatabaseWarmupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Pre-condition: the warmup is parked in WaitForDatabaseReadyAsync.
        Assert.Equal(0, scopeFactory.ScopesCreated);

        startupStatus.RecordDatabaseReady();

        // Poll for the warmup to land. Polling (rather than calling StopAsync
        // immediately) avoids a race where the cancellation from StopAsync
        // arrives before Task.Run has dispatched the post-ready continuation.
        await WaitForAsync(
            () => scopeFactory.ScopesCreated >= 1,
            TimeSpan.FromSeconds(2),
            "Warmup did not open a DI scope after the database-ready signal.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Warmup_DoesNotThrow_WhenDatabaseFailsToInitialize()
    {
        var startupStatus = new StartupStatus();
        await using var provider = BuildProvider();
        var scopeFactory = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var service = new DatabaseWarmupHostedService(
            scopeFactory,
            startupStatus,
            NullLogger<DatabaseWarmupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        startupStatus.RecordDatabaseFailure("simulated migration timeout");

        // StopAsync must complete cleanly even though the wait faulted; the
        // service must absorb the InvalidOperationException raised by
        // WaitForDatabaseReadyAsync — never propagate it.
        await service.StopAsync(CancellationToken.None);

        // No DI scope should have been opened — we skipped the warmup queries.
        Assert.Equal(0, scopeFactory.ScopesCreated);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail(failureMessage);
    }

    private static ServiceProvider BuildProvider(Action<ShowroomBillingDbContext>? seed = null)
    {
        var services = new ServiceCollection();
        // Each test gets its own isolated InMemory database.
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<ShowroomBillingDbContext>(options => options.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();

        if (seed is not null)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ShowroomBillingDbContext>();
            seed(db);
        }
        return provider;
    }

    /// <summary>
    /// Wraps a real <see cref="IServiceScopeFactory"/> and counts how many scopes
    /// the warmup service opens. Used to assert the warmup gates correctly on
    /// the database-ready signal without inspecting internal state.
    /// </summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int ScopesCreated { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            return inner.CreateScope();
        }
    }
}
