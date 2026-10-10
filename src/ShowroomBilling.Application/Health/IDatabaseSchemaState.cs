namespace ShowroomBilling.Application.Health;

/// <summary>
/// What this process last learned about the schema of the database it runs
/// against: how many of this build's migrations the database has not had.
/// <para>
/// A build on a database that is behind answers every bill and numbering
/// request with an error while PostgreSQL itself is perfectly reachable, so
/// "reachable" alone is not health. The start-up migration and each full
/// health check record what they find here, and the cheap health probe reports
/// the last value without touching the database. In memory only.
/// </para>
/// </summary>
public interface IDatabaseSchemaState
{
    /// <summary>Null: not looked at yet. Zero: up to date.</summary>
    int? PendingMigrationCount { get; }

    void Record(int pendingMigrationCount);
}
