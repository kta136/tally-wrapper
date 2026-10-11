namespace ShowroomBilling.Application.Admin;

public sealed class AdminPasscodeNotConfiguredException() : Exception("Admin passcode has not been configured.");

public sealed class AdminPasscodeInvalidException() : Exception("Admin passcode is invalid.");

/// <summary>
/// The request is allowed only from an unlocked admin session, and the
/// endpoint is not admin-only as a whole (editing a posted bill, for example).
/// </summary>
public sealed class AdminSessionRequiredException(string message) : Exception(message);

public sealed class AdminUnlockThrottledException(TimeSpan retryAfter)
    : Exception($"Too many failed unlock attempts. Try again in {(int)Math.Ceiling(retryAfter.TotalSeconds)} second(s).")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
