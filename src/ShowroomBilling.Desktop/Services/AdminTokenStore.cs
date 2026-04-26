using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Desktop.Services;

public sealed class AdminTokenStore
{
    private readonly object _lock = new();
    private AdminUnlockResponse? _current;

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
        }
        Changed?.Invoke(unlock);
    }

    public void Clear()
    {
        AdminUnlockResponse? previous;
        lock (_lock)
        {
            previous = _current;
            _current = null;
        }
        if (previous is not null)
        {
            Changed?.Invoke(null);
        }
    }
}
