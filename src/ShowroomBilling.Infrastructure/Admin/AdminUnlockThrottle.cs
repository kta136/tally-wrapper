using ShowroomBilling.Application.Admin;

namespace ShowroomBilling.Infrastructure.Admin;

/// <summary>
/// In-memory backoff: first 4 failures are free, then an exponential cooldown
/// kicks in (2s, 4s, 8s, 16s, capped at 30s). The counter resets on a
/// successful unlock or API restart.
/// </summary>
public sealed class AdminUnlockThrottle : IAdminUnlockThrottle
{
    private const int FreeAttempts = 4;
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, ThrottleState> _state = new();

    public TimeSpan TryBeginAttempt(Guid showroomId)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _state.TryGetValue(showroomId, out var current);
            if (current is not null && current.NextAllowedUtc > now)
            {
                return current.NextAllowedUtc - now;
            }

            var attempts = (current?.FailedAttempts ?? 0) + 1;
            _state[showroomId] = new ThrottleState(attempts, now + ComputeCooldown(attempts));
            return TimeSpan.Zero;
        }
    }

    public void Reset(Guid showroomId)
    {
        lock (_gate)
        {
            _state.Remove(showroomId);
        }
    }

    private static TimeSpan ComputeCooldown(int failedAttempts)
    {
        if (failedAttempts <= FreeAttempts)
        {
            return TimeSpan.Zero;
        }
        // 5th fail → 2s, 6th → 4s, 7th → 8s, 8th → 16s, 9th+ → 30s cap.
        var seconds = Math.Min(MaxCooldown.TotalSeconds, Math.Pow(2, failedAttempts - FreeAttempts));
        return TimeSpan.FromSeconds(seconds);
    }

    private sealed record ThrottleState(int FailedAttempts, DateTimeOffset NextAllowedUtc);
}
