using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests.Postgres;

/// <summary>
/// Base for a test class whose tests each need an empty, fully migrated
/// PostgreSQL database. xUnit builds a new instance per test, so every test
/// gets its own database and drops it afterwards.
/// </summary>
[Trait("Category", "Postgres")]
public abstract class DatabaseTest(PostgresFixture postgres) : IAsyncLifetime
{
    private PostgresFixture.PostgresTestDatabase? database;

    protected PostgresFixture.PostgresTestDatabase Database =>
        database ?? throw new InvalidOperationException("The test database is created in InitializeAsync.");

    /// <summary>A new context on this test's database.</summary>
    protected ShowroomBillingDbContext CreateDbContext() => Database.CreateContext();

    public virtual async ValueTask InitializeAsync() =>
        database = await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken);

    public virtual async ValueTask DisposeAsync()
    {
        if (database is not null)
        {
            await database.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
