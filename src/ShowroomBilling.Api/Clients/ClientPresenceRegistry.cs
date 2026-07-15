using ShowroomBilling.Contracts.Clients;

namespace ShowroomBilling.Api.Clients;

public sealed class ClientPresenceRegistry
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private const int MaxEntries = 200;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ClientPresenceResponse Register(ClientHeartbeatRequest request, string remoteAddress)
    {
        var now = DateTimeOffset.UtcNow;
        var deviceId = Normalize(request.DeviceId, "unknown");

        lock (_gate)
        {
            PruneExpired(now);

            if (_entries.TryGetValue(deviceId, out var existing))
            {
                existing = existing with
                {
                    CounterName = Normalize(request.CounterName, "Counter"),
                    AppVersion = Normalize(request.AppVersion, "unknown"),
                    ConnectionMode = Normalize(request.ConnectionMode, "unknown"),
                    MachineName = Normalize(request.MachineName, "unknown"),
                    UserName = Normalize(request.UserName, "unknown"),
                    RemoteAddress = remoteAddress,
                    LastSeenAtUtc = now
                };
            }
            else
            {
                existing = new Entry(
                    DeviceId: deviceId,
                    CounterName: Normalize(request.CounterName, "Counter"),
                    AppVersion: Normalize(request.AppVersion, "unknown"),
                    ConnectionMode: Normalize(request.ConnectionMode, "unknown"),
                    MachineName: Normalize(request.MachineName, "unknown"),
                    UserName: Normalize(request.UserName, "unknown"),
                    RemoteAddress: remoteAddress,
                    FirstSeenAtUtc: now,
                    LastSeenAtUtc: now);
            }

            _entries[deviceId] = existing;
            TrimOverflow();
            return ToResponse(existing, now);
        }
    }

    public int ActiveCount
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
            {
                PruneExpired(now);
                return _entries.Count;
            }
        }
    }

    public ClientPresenceListResponse Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            PruneExpired(now);
            var clients = _entries.Values
                .OrderByDescending(x => x.LastSeenAtUtc)
                .Select(x => ToResponse(x, now))
                .ToArray();
            return new ClientPresenceListResponse(now, clients);
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var key in _entries
            .Where(pair => pair.Value.LastSeenAtUtc.Add(Ttl) <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private void TrimOverflow()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries.Values
            .OrderBy(x => x.LastSeenAtUtc)
            .Take(_entries.Count - MaxEntries)
            .Select(x => x.DeviceId)
            .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private static ClientPresenceResponse ToResponse(Entry entry, DateTimeOffset now) =>
        new(
            entry.DeviceId,
            entry.CounterName,
            entry.AppVersion,
            entry.ConnectionMode,
            entry.MachineName,
            entry.UserName,
            entry.RemoteAddress,
            entry.FirstSeenAtUtc,
            entry.LastSeenAtUtc,
            entry.LastSeenAtUtc.Add(Ttl));

    private static string Normalize(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private sealed record Entry(
        string DeviceId,
        string CounterName,
        string AppVersion,
        string ConnectionMode,
        string MachineName,
        string UserName,
        string RemoteAddress,
        DateTimeOffset FirstSeenAtUtc,
        DateTimeOffset LastSeenAtUtc);
}
