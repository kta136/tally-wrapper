using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Health;

public sealed class DatabaseSchemaState : IDatabaseSchemaState
{
    private const int Unknown = -1;
    private int _pending = Unknown;

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
        IDatabaseSchemaState state,
        CancellationToken cancellationToken)
    {
        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
        state.Record(pending);
        return pending;
    }
}
