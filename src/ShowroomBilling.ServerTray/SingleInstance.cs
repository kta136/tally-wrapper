namespace ShowroomBilling.ServerTray;

/// <summary>How an install or repair went, for the one message the caller shows about it.</summary>
public enum InstallOutcome
{
    /// <summary>Setup ran to the end, or there was nothing to do.</summary>
    Done,

    /// <summary>Setup never ran: the range prompt was cancelled, or Windows was not given permission.</summary>
    NotStarted,

    /// <summary>Setup ran and failed; the install log has the reason.</summary>
    Failed,
}

/// <summary>
/// The one tray a Windows session may have. A second copy of the exe asks the
/// first to close and waits for it, then becomes the tray: running the exe
/// again is how the server is repaired and upgraded, so the copy just started
/// is the one that should stay. The names are per session (<c>Local\</c>),
/// because the tray starts at each user's sign-in.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\TallyWrapper.ServerTray";
    private const string HandOverName = @"Local\TallyWrapper.ServerTray.HandOver";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _handOver;
    private RegisteredWaitHandle? _waiting;

    private SingleInstance(Mutex mutex, EventWaitHandle handOver)
    {
        _mutex = mutex;
        _handOver = handOver;
    }

    /// <summary>
    /// Takes the tray's place. Null when another copy holds it and did not
    /// close within <paramref name="wait"/>. Call it, and dispose the result,
    /// on the same thread: a mutex belongs to the thread that took it.
    /// </summary>
    /// <param name="tookOver">True when another copy was running and made way.</param>
    public static SingleInstance? Acquire(TimeSpan wait, out bool tookOver) =>
        Acquire(MutexName, HandOverName, wait, out tookOver);

    internal static SingleInstance? Acquire(string mutexName, string handOverName, TimeSpan wait, out bool tookOver)
    {
        tookOver = false;
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var handOver = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, handOverName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                tookOver = true;
                handOver.Set();
                if (!mutex.WaitOne(wait))
                {
                    mutex.Dispose();
                    handOver.Dispose();
                    return null;
                }
            }
        }
        catch (AbandonedMutexException)
        {
            // The copy that held it ended without letting go. It is ours now.
        }

        return new SingleInstance(mutex, handOver);
    }

    /// <summary>Calls <paramref name="close"/>, once and on a pool thread, when a later copy asks this one to make way.</summary>
    public void OnAskedToHandOver(Action close) =>
        _waiting = ThreadPool.RegisterWaitForSingleObject(
            _handOver, (_, _) => close(), state: null, Timeout.Infinite, executeOnlyOnce: true);

    public void Dispose()
    {
        _waiting?.Unregister(null);
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held by this thread; there is nothing to give back.
        }

        _mutex.Dispose();
        _handOver.Dispose();
    }
}
