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
    /// Atomically checks the cooldown and, when none applies, counts this
    /// attempt before the passcode is verified. Counting up front means
    /// concurrent guesses cannot all slip past the check before any failure
    /// is recorded. Returns the remaining cooldown when the attempt is refused,
    /// or <see cref="TimeSpan.Zero"/> when it may proceed.
    /// </summary>
    TimeSpan TryBeginAttempt(Guid showroomId);

    /// <summary>
    /// Reset the counter for <paramref name="showroomId"/>. Call when the
    /// passcode verified.
    /// </summary>
    void Reset(Guid showroomId);
}

public sealed class AdminUnlockThrottledException(TimeSpan retryAfter)
    : Exception($"Too many failed unlock attempts. Try again in {(int)Math.Ceiling(retryAfter.TotalSeconds)} second(s).")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
