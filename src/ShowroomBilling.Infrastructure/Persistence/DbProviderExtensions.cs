using Microsoft.EntityFrameworkCore;

namespace ShowroomBilling.Infrastructure.Persistence;

internal static class DbProviderExtensions
{
    private const string InMemoryProviderName = "Microsoft.EntityFrameworkCore.InMemory";

    /// <summary>
    /// True under the EF InMemory provider the unit tests run on. It has no
    /// transactions, row locks, bulk updates or raw SQL, so a few code paths
    /// take a simpler route there. Those routes are test-only: production
    /// behaviour is covered by the Postgres suite (see CLAUDE.md,
    /// "Test-harness reality").
    /// </summary>
    internal static bool UsesInMemoryProvider(this DbContext dbContext) =>
        string.Equals(dbContext.Database.ProviderName, InMemoryProviderName, StringComparison.Ordinal);
}
