using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Infrastructure.Settings;

/// <summary>
/// Holds the effective settings for a few seconds. Almost every request reads
/// them and they rarely change; a write clears the cache straight away. One
/// instance per API process (registered as a singleton); a
/// <see cref="CloudSettingsService"/> built without one, as in tests, reads the
/// database every time.
/// </summary>
public sealed class EffectiveSettingsCache(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private EffectiveSettingsResponse? _settings;
    private DateTimeOffset _validUntilUtc;

    public bool TryGet(out EffectiveSettingsResponse settings)
    {
        lock (_gate)
        {
            if (_settings is not null && _timeProvider.GetUtcNow() < _validUntilUtc)
            {
                settings = _settings;
                return true;
            }
        }

        settings = null!;
        return false;
    }

    public void Set(EffectiveSettingsResponse settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _validUntilUtc = _timeProvider.GetUtcNow().Add(TimeToLive);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _settings = null;
            _validUntilUtc = default;
        }
    }
}
