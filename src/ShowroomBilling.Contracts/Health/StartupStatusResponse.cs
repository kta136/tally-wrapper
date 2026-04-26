namespace ShowroomBilling.Contracts.Health;

/// <summary>
/// Wire shape for <c>GET /api/health/startup</c>. Exposes whether the DB
/// migration and stuck-posting recovery hosted services completed cleanly,
/// so the Desktop can surface a degraded banner instead of failing opaquely
/// when the API came up but DB-backed features are unavailable.
/// </summary>
public sealed record StartupStatusResponse(
    DateTimeOffset StartedAtUtc,
    bool DatabaseReady,
    string? DatabaseError,
    bool RecoveryRan,
    int RecoveryHealedCount,
    string? RecoveryError);
