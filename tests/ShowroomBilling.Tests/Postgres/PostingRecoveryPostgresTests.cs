using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostingRecoveryPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RecoverAsync_OnlyRecoversClaimsOlderThanAnyLivePush()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        Guid abandoned, live;
        await using (var db = database.CreateContext())
        {
            var service = PostgresBillTestSupport.BuildService(db);
            abandoned = (await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m)))).Id;
            live = (await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("B", 100m)))).Id;
            await db.Bills.Where(b => b.Id == abandoned).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BillStates.Posting)
                .SetProperty(b => b.UpdatedAtUtc, now.AddMinutes(-20)));
            await db.Bills.Where(b => b.Id == live).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.State, BillStates.Posting)
                .SetProperty(b => b.UpdatedAtUtc, now.AddMinutes(-1)));
        }

        await using var recoveryDb = database.CreateContext();
        var (recovered, nextCheck) = await AbandonedPostingRecovery.RecoverAsync(recoveryDb, now, CancellationToken.None);

        await using var verify = database.CreateContext();
        var states = await verify.Bills.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.State);
        Assert.Equal(1, recovered);
        Assert.Equal(BillStates.ReconciliationRequired, states[abandoned]);
        Assert.Equal(BillStates.Posting, states[live]);
        Assert.NotNull(nextCheck);
        Assert.InRange(nextCheck!.Value, now.AddMinutes(8), now.AddMinutes(10));
        Assert.Equal(1, await verify.AuditEvents.CountAsync(a => a.EventType == "bill.posting.recovered"));
    }

    [Fact]
    public async Task A_bill_stranded_while_the_service_is_running_is_recovered_without_a_restart()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var sweeper = new Sweeper(database);
        sweeper.StartupStatus.RecordDatabaseReady();
        await sweeper.StartAsync();
        await sweeper.WaitForSweepsAsync(1);

        // The start-up sweep is over and found nothing. Now a push loses the
        // database after Tally has answered: its claim stays behind.
        var stranded = await StrandAsync(database, claimedAt: sweeper.Clock.GetUtcNow());

        // A claim that young may be a live push, so the next sweeps leave it.
        sweeper.Clock.Advance(StuckPostingRecoveryHostedService.SweepInterval);
        await sweeper.WaitForSweepsAsync(2);
        Assert.Equal(BillStates.Posting, await StateOfAsync(database, stranded));

        await sweeper.AdvanceUntilAsync(async () =>
            await StateOfAsync(database, stranded) == BillStates.ReconciliationRequired);

        await using var verify = database.CreateContext();
        Assert.Equal(1, await verify.AuditEvents.CountAsync(a =>
            a.EventType == "bill.posting.recovered" && a.EntityId == stranded.ToString()));
    }

    [Fact]
    public async Task The_sweeps_run_even_when_the_database_was_not_ready_at_start_up()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var sweeper = new Sweeper(database);
        var stranded = await StrandAsync(database, claimedAt: sweeper.Clock.GetUtcNow().AddMinutes(-20));

        // What the initialiser records when PostgreSQL does not answer at boot.
        sweeper.StartupStatus.RecordDatabaseFailure("PostgreSQL was unreachable at start-up.");
        await sweeper.StartAsync();

        await sweeper.AdvanceUntilAsync(async () =>
            await StateOfAsync(database, stranded) == BillStates.ReconciliationRequired);
    }

    [Fact]
    public async Task A_sweep_that_fails_is_followed_by_the_next_one()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var sweeper = new Sweeper(database, failingSweeps: 1);
        var stranded = await StrandAsync(database, claimedAt: sweeper.Clock.GetUtcNow().AddMinutes(-20));
        sweeper.StartupStatus.RecordDatabaseReady();
        await sweeper.StartAsync();

        await sweeper.WaitForSweepsAsync(1);
        Assert.Equal(BillStates.Posting, await StateOfAsync(database, stranded));

        await sweeper.AdvanceUntilAsync(async () =>
            await StateOfAsync(database, stranded) == BillStates.ReconciliationRequired);
    }

    [Fact]
    public async Task No_bill_is_read_while_the_database_is_behind_its_migrations()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using var sweeper = new Sweeper(database);
        var stranded = await StrandAsync(database, claimedAt: sweeper.Clock.GetUtcNow().AddMinutes(-20));
        sweeper.SchemaState.Record(3);
        sweeper.StartupStatus.RecordDatabaseFailure("3 migration(s) pending.");
        await sweeper.StartAsync();

        for (var i = 0; i < 3; i++)
        {
            sweeper.Clock.Advance(StuckPostingRecoveryHostedService.SweepInterval);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, sweeper.SweepsStarted);
        Assert.Equal(BillStates.Posting, await StateOfAsync(database, stranded));

        // The owner applies the migrations; the next health check records it.
        sweeper.SchemaState.Record(0);

        await sweeper.AdvanceUntilAsync(async () =>
            await StateOfAsync(database, stranded) == BillStates.ReconciliationRequired);
    }

    private static async Task<Guid> StrandAsync(PostgresFixture.PostgresTestDatabase database, DateTimeOffset claimedAt)
    {
        await using var db = database.CreateContext();
        var bill = await PostgresBillTestSupport.BuildService(db).CreateDraftAsync(
            new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("Stranded", 100m)));
        await db.Bills.Where(b => b.Id == bill.Id).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.State, BillStates.Posting)
            .SetProperty(b => b.UpdatedAtUtc, claimedAt));
        return bill.Id;
    }

    private static async Task<string> StateOfAsync(PostgresFixture.PostgresTestDatabase database, Guid billId)
    {
        await using var db = database.CreateContext();
        return await db.Bills.AsNoTracking().Where(b => b.Id == billId).Select(b => b.State).SingleAsync();
    }

    /// <summary>
    /// The recovery service on a test database, with a clock the test moves.
    /// Disposing it stops the service and fails the test if anything escaped
    /// into the host.
    /// </summary>
    private sealed class Sweeper : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CountingScopeFactory _scopes;
        private readonly StuckPostingRecoveryHostedService _service;

        public Sweeper(PostgresFixture.PostgresTestDatabase database, int failingSweeps = 0)
        {
            var services = new ServiceCollection();
            services.AddDbContext<ShowroomBillingDbContext>(options =>
                options.UseNpgsql(database.ConnectionString, npgsql => npgsql.SetPostgresVersion(18, 0)));
            _provider = services.BuildServiceProvider();
            _scopes = new CountingScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>(), failingSweeps);
            _service = new StuckPostingRecoveryHostedService(
                _scopes,
                StartupStatus,
                SchemaState,
                NullLogger<StuckPostingRecoveryHostedService>.Instance,
                Clock);
        }

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

        public StartupStatus StartupStatus { get; } = new();

        public DatabaseSchemaState SchemaState { get; } = new();

        public int SweepsStarted => _scopes.Started;

        public Task StartAsync() => _service.StartAsync(TestContext.Current.CancellationToken);

        /// <summary>Waits until that many sweeps have finished, without moving the clock.</summary>
        public async Task WaitForSweepsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (_scopes.Finished < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"Only {_scopes.Finished} of {count} sweep(s) finished.");
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            // The service is now between that sweep and its wait for the next.
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        /// <summary>Moves the clock on a sweep interval at a time until the condition holds.</summary>
        public async Task AdvanceUntilAsync(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!await condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "The sweeps did not get there in time.");
                Clock.Advance(StuckPostingRecoveryHostedService.SweepInterval);
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _service.StopAsync(CancellationToken.None);
            var execution = _service.ExecuteTask;
            _service.Dispose();
            await _provider.DisposeAsync();
            Assert.True(execution is null or { IsCompletedSuccessfully: true }, "The service let an exception out.");
        }
    }

    /// <summary>Counts the sweeps (each opens one scope) and makes the first few fail.</summary>
    private sealed class CountingScopeFactory(IServiceScopeFactory inner, int failingSweeps) : IServiceScopeFactory
    {
        private int _started;
        private int _finished;

        public int Started => Volatile.Read(ref _started);

        public int Finished => Volatile.Read(ref _finished);

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _started) <= failingSweeps)
            {
                Interlocked.Increment(ref _finished);
                // Not a database error: EF and LINQ throw this type for real bugs too.
                throw new InvalidOperationException("Sequence contains no elements");
            }

            return new CountedScope(inner.CreateScope(), () => Interlocked.Increment(ref _finished));
        }

        private sealed class CountedScope(IServiceScope scope, Action onDisposed) : IServiceScope
        {
            public IServiceProvider ServiceProvider => scope.ServiceProvider;

            public void Dispose()
            {
                scope.Dispose();
                onDisposed();
            }
        }
    }
}
