using ShowroomBilling.Application.Health;

namespace ShowroomBilling.Infrastructure.Health;

/// <summary>
/// In-process implementation. Nothing is persisted: each boot re-runs the
/// hosted services and signals again.
/// </summary>
public sealed class StartupStatus : IStartupStatus
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

    public Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            return _databaseReadyTcs.Task.WaitAsync(cancellationToken);
        }
        return _databaseReadyTcs.Task;
    }
}
