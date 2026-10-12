namespace ShowroomBilling.Infrastructure.Health;

/// <summary>What one database health check found.</summary>
/// <param name="Reachable">Whether PostgreSQL answered.</param>
/// <param name="Identity">The database's own <c>environment</c> marker; null when it did not answer or has none.</param>
/// <param name="PendingMigrationCount">Migrations of this build the database has not had; the last known count when it did not answer.</param>
public sealed record DatabaseHealthReading(
    bool Reachable,
    string? Identity,
    int? PendingMigrationCount);

/// <summary>
/// Holds the last database health check for a few seconds. Every counter and
/// the server tray ask for health every 15 seconds, and each answer is a real
/// check of the database; with this they share one check instead of making
/// one each. One instance per API process (registered as a singleton).
/// </summary>
public sealed class DatabaseHealthCache(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DatabaseHealthReading? _reading;
    private DateTimeOffset _validUntilUtc;

    public bool TryGet(out DatabaseHealthReading reading)
    {
        lock (_gate)
        {
            if (_reading is not null && _timeProvider.GetUtcNow() < _validUntilUtc)
            {
                reading = _reading;
                return true;
            }
        }

        reading = null!;
        return false;
    }

    /// <returns>
    /// The reading this one replaces, however old, or null for the first. The
    /// caller compares the two to log a change once, not once per poll.
    /// </returns>
    public DatabaseHealthReading? Set(DatabaseHealthReading reading)
    {
        lock (_gate)
        {
            var before = _reading;
            _reading = reading;
            _validUntilUtc = _timeProvider.GetUtcNow().Add(TimeToLive);
            return before;
        }
    }
}
