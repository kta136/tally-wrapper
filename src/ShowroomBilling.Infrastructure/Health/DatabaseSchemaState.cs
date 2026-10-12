using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Health;

/// <summary>
/// What this process last learned about the schema of the database it runs
/// against: how many of this build's migrations the database has not had.
/// <para>
/// A build on a database that is behind answers every bill and numbering
/// request with an error while PostgreSQL itself is perfectly reachable, so
/// "reachable" alone is not health. The start-up migration and each health
/// check record what they find here; it is what health reports while the
/// database does not answer, and what the stuck-posting sweep looks at before
/// it reads a bill. In memory only.
/// </para>
/// </summary>
public sealed class DatabaseSchemaState
{
    private const int Unknown = -1;
    private int _pending = Unknown;

    /// <summary>Null: not looked at yet. Zero: up to date.</summary>
    public int? PendingMigrationCount =>
        Volatile.Read(ref _pending) is var pending && pending == Unknown ? null : pending;

    public void Record(int pendingMigrationCount) =>
        Volatile.Write(ref _pending, Math.Max(0, pendingMigrationCount));

    /// <summary>
    /// Asks the database which of this build's migrations it has not had and
    /// records the answer.
    /// </summary>
    public static async Task<int> CheckAsync(
        ShowroomBillingDbContext dbContext,
        DatabaseSchemaState state,
        CancellationToken cancellationToken)
    {
        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        state.Record(pending);
        return pending;
    }
}
