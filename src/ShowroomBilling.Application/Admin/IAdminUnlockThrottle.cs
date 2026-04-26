namespace ShowroomBilling.Application.Admin;

/// <summary>
/// In-process backoff for failed admin-unlock attempts. Singleton-scoped so
/// state survives across the per-request scoped <see cref="IAdminAuthService"/>.
///
/// Threat model: device-token auth (slice 2c) already blocks other local
/// processes from reaching this endpoint, so the realistic attacker is an
/// operator who forgot the passcode rather than a brute-force script. The
/// throttle is "annoy a forgetful human" rather than "stop a network attack."
/// State resets on API restart by design — persisted attempt counters are a
/// follow-up if the threat model ever changes.
/// </summary>
public interface IAdminUnlockThrottle
{
    /// <summary>
    /// Returns the cooldown remaining for <paramref name="showroomId"/>, or
    /// <see cref="TimeSpan.Zero"/> when the next attempt is allowed now.
    /// </summary>
    TimeSpan GetCooldown(Guid showroomId);

    /// <summary>
    /// Record a failed unlock and return the cooldown that now applies for
    /// the *next* attempt (used so callers can put the duration in the audit /
    /// log line).
    /// </summary>
    TimeSpan RegisterFailure(Guid showroomId);

    /// <summary>
    /// Reset the counter for <paramref name="showroomId"/>. Call on a
    /// successful unlock.
    /// </summary>
    void Reset(Guid showroomId);
}

public sealed class AdminUnlockThrottledException(TimeSpan retryAfter)
    : Exception($"Too many failed unlock attempts. Try again in {(int)Math.Ceiling(retryAfter.TotalSeconds)} second(s).")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
