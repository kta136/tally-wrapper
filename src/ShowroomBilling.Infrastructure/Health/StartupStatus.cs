using ShowroomBilling.Application.Health;

namespace ShowroomBilling.Infrastructure.Health;

/// <summary>
/// The signal the database initialiser gives the other start-up services:
/// migrations finished, or they failed. In memory only; every API boot starts
/// again. What went wrong is in the API log, and <c>GET /api/health/ready</c>
/// reports whether the database answers now.
/// </summary>
public sealed class StartupStatus
{
    private readonly TaskCompletionSource _databaseReadyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RecordDatabaseReady() => _databaseReadyTcs.TrySetResult();

    public void RecordDatabaseFailure(string error)
    {
        // Awaiters of WaitForDatabaseReadyAsync (e.g. stuck-posting recovery) get
        // a fault rather than hanging forever — they should treat it as "skip this run".
        _databaseReadyTcs.TrySetException(new DatabaseNotReadyException(error));
    }

    /// <summary>
    /// Completes when <see cref="RecordDatabaseReady"/> has been called and
    /// faults with <see cref="DatabaseNotReadyException"/> when
    /// <see cref="RecordDatabaseFailure"/> has. A background start-up service
    /// waits on it so that it never runs against an unmigrated schema.
    /// </summary>
    public Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            return _databaseReadyTcs.Task.WaitAsync(cancellationToken);
        }
        return _databaseReadyTcs.Task;
    }
}
