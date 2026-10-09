using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Desktop.Services;

public sealed class AdminTokenStore
{
    private readonly object _lock = new();
    private AdminUnlockResponse? _current;
    private Timer? _expiryTimer;
    // The session the timer belongs to. Current's getter may already have
    // dropped an expired _current, so the timer checks this instead.
    private AdminUnlockResponse? _scheduled;

    public event Action<AdminUnlockResponse?>? Changed;

    public AdminUnlockResponse? Current
    {
        get
        {
            lock (_lock)
            {
                if (_current is not null && _current.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    _current = null;
                }
                return _current;
            }
        }
    }

    public bool IsUnlocked => Current is not null;

    public void Set(AdminUnlockResponse unlock)
    {
        lock (_lock)
        {
            _current = unlock;
            _scheduled = unlock;
            ScheduleExpiry(unlock, SynchronizationContext.Current);
        }
        Changed?.Invoke(unlock);
    }

    /// <summary>
    /// Raises <see cref="Changed"/> when the session expires, so admin-only
    /// UI hides itself instead of staying lit after the 30-minute TTL.
    /// Raised on the context that unlocked (the UI thread).
    /// </summary>
    private void ScheduleExpiry(AdminUnlockResponse unlock, SynchronizationContext? context)
    {
        _expiryTimer?.Dispose();
        var dueIn = unlock.ExpiresAtUtc - DateTimeOffset.UtcNow;
        if (dueIn < TimeSpan.Zero) dueIn = TimeSpan.Zero;
        _expiryTimer = new Timer(_ =>
        {
            void Expire()
            {
                bool expired;
                lock (_lock)
                {
                    expired = ReferenceEquals(_scheduled, unlock);
                    if (expired)
                    {
                        _current = null;
                        _scheduled = null;
                    }
                }
                if (expired) Changed?.Invoke(null);
            }

            if (context is null) Expire();
            else context.Post(_ => Expire(), null);
        }, null, dueIn, Timeout.InfiniteTimeSpan);
    }

    public void Clear()
    {
        AdminUnlockResponse? previous;
        lock (_lock)
        {
            previous = _current;
            _current = null;
            _scheduled = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
        }
        if (previous is not null)
        {
            Changed?.Invoke(null);
        }
    }
}
