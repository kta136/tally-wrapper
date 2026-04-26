using System.Collections.Concurrent;
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

    private readonly ConcurrentDictionary<Guid, ThrottleState> _state = new();

    public TimeSpan GetCooldown(Guid showroomId)
    {
        if (!_state.TryGetValue(showroomId, out var state))
        {
            return TimeSpan.Zero;
        }
        var remaining = state.NextAllowedUtc - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public TimeSpan RegisterFailure(Guid showroomId)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = _state.AddOrUpdate(
            showroomId,
            _ => new ThrottleState(FailedAttempts: 1, NextAllowedUtc: now),
            (_, current) =>
            {
                var attempts = current.FailedAttempts + 1;
                var cooldown = ComputeCooldown(attempts);
                return new ThrottleState(attempts, now + cooldown);
            });
        return updated.NextAllowedUtc - now;
    }

    public void Reset(Guid showroomId) => _state.TryRemove(showroomId, out _);

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
