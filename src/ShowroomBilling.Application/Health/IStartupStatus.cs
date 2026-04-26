using ShowroomBilling.Contracts.Health;

namespace ShowroomBilling.Application.Health;

/// <summary>
/// Singleton scratchpad for the API's startup hosted services. Each service
/// updates its own slot; the Health controller exposes the snapshot via
/// <c>GET /api/health/startup</c>. The Desktop reads this to decide whether
/// to show a "limited mode — database unavailable" banner.
/// </summary>
public interface IStartupStatus
{
    StartupStatusResponse Snapshot();

    void RecordDatabaseReady();
    void RecordDatabaseFailure(string error);

    void RecordRecoveryComplete(int healedBills);
    void RecordRecoveryFailure(string error);

    /// <summary>
    /// Completes successfully when <see cref="RecordDatabaseReady"/> has been
    /// called, or faults when <see cref="RecordDatabaseFailure"/> is called.
    /// Lets background hosted services (e.g. stuck-posting recovery) defer work
    /// until migrations have finished, so the API can accept traffic before
    /// recovery is done without the recovery service hitting an unmigrated schema.
    /// </summary>
    Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default);
}
