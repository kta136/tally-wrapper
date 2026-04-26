using ShowroomBilling.Application.Health;
using ShowroomBilling.Contracts.Health;

namespace ShowroomBilling.Infrastructure.Health;

/// <summary>
/// In-process implementation. State lives only in memory and resets when the
/// API restarts, which is correct: each boot re-runs the hosted services and
/// re-populates this object. No DB persistence needed.
/// </summary>
public sealed class StartupStatus : IStartupStatus
{
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _databaseReadyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _databaseReady;
    private string? _databaseError;

    private bool _recoveryRan;
    private int _recoveryHealedCount;
    private string? _recoveryError;

    public StartupStatusResponse Snapshot()
    {
        lock (_gate)
        {
            return new StartupStatusResponse(
                StartedAtUtc: _startedAtUtc,
                DatabaseReady: _databaseReady,
                DatabaseError: _databaseError,
                RecoveryRan: _recoveryRan,
                RecoveryHealedCount: _recoveryHealedCount,
                RecoveryError: _recoveryError);
        }
    }

    public void RecordDatabaseReady()
    {
        lock (_gate)
        {
            _databaseReady = true;
            _databaseError = null;
        }
        _databaseReadyTcs.TrySetResult();
    }

    public void RecordDatabaseFailure(string error)
    {
        lock (_gate)
        {
            _databaseReady = false;
            _databaseError = error;
        }
        // Awaiters of WaitForDatabaseReadyAsync (e.g. stuck-posting recovery) get
        // a fault rather than hanging forever — they should treat it as "skip this run".
        _databaseReadyTcs.TrySetException(new InvalidOperationException(error));
    }

    public Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            return _databaseReadyTcs.Task.WaitAsync(cancellationToken);
        }
        return _databaseReadyTcs.Task;
    }

    public void RecordRecoveryComplete(int healedBills)
    {
        lock (_gate)
        {
            _recoveryRan = true;
            _recoveryHealedCount = healedBills;
            _recoveryError = null;
        }
    }

    public void RecordRecoveryFailure(string error)
    {
        lock (_gate)
        {
            _recoveryRan = false;
            _recoveryError = error;
        }
    }
}
