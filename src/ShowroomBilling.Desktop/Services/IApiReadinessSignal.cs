namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Singleton signal that goes from "not yet ready" to "ready" exactly once,
/// at the moment <see cref="MainWindow.OnLoaded"/> finishes its TCP probe of
/// the API child process. ViewModels and other consumers can <c>await</c>
/// <see cref="WhenReadyAsync"/> to defer work that must not run before the
/// API is reachable — without each consumer re-implementing its own probe.
///
/// This sits alongside <c>IStartupStatus</c> (server-side) — that one tracks
/// readiness inside the API process; this one tracks readiness from the
/// Desktop's perspective (the API child has bound a port the Desktop can
/// reach). They are deliberately separate concerns.
/// </summary>
public interface IApiReadinessSignal
{
    /// <summary>
    /// Completes when <see cref="MarkReady"/> has been called. Honors the
    /// supplied <paramref name="cancellationToken"/> so awaiters can bound
    /// their wait — callers should pair it with a reasonable timeout
    /// (typically ~10 s) to avoid hanging if the API never comes up.
    /// </summary>
    Task WhenReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot accessor — true after <see cref="MarkReady"/> has fired.
    /// Useful for fast-path branches that don't want to allocate a Task.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Idempotent. Subsequent calls are no-ops.
    /// </summary>
    void MarkReady();
}

/// <summary>
/// In-process implementation. State lives only in memory and resets when the
/// Desktop process restarts, which is correct: each launch re-runs the
/// readiness probe.
/// </summary>
public sealed class ApiReadinessSignal : IApiReadinessSignal
{
    private readonly TaskCompletionSource _readyTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _readyTcs.Task.IsCompletedSuccessfully;

    public Task WhenReadyAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.CanBeCanceled)
        {
            return _readyTcs.Task.WaitAsync(cancellationToken);
        }
        return _readyTcs.Task;
    }

    public void MarkReady() => _readyTcs.TrySetResult();
}
