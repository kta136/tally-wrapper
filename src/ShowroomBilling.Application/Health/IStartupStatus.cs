namespace ShowroomBilling.Application.Health;

/// <summary>
/// The signal the database initialiser gives the other start-up services:
/// migrations finished, or they failed. In memory only; every API boot starts
/// again. What went wrong is in the API log, and <c>GET /api/health/ready</c>
/// reports whether the database answers now.
/// </summary>
public interface IStartupStatus
{
    void RecordDatabaseReady();
    void RecordDatabaseFailure(string error);

    /// <summary>
    /// Completes successfully when <see cref="RecordDatabaseReady"/> has been
    /// called, or faults when <see cref="RecordDatabaseFailure"/> is called.
    /// Lets background hosted services (e.g. stuck-posting recovery) defer work
    /// until migrations have finished, so the API can accept traffic before
    /// recovery is done without the recovery service hitting an unmigrated schema.
    /// </summary>
    Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default);
}
