namespace ShowroomBilling.Contracts.Health;

/// <summary>Values of <see cref="TallyCompanyHealthResponse.Status"/>.</summary>
public static class TallyHealthStatuses
{
    /// <summary>Tally answered and the configured active company is open.</summary>
    public const string Healthy = "healthy";

    /// <summary>Tally answered but the active company is not open, or none is configured.</summary>
    public const string Warning = "warning";

    /// <summary>Tally could not be reached, or settings could not be read.</summary>
    public const string Unhealthy = "unhealthy";
}

/// <summary>Freshness of a stored Tally master snapshot, and of all four together.</summary>
public static class MasterFreshness
{
    public const string Fresh = "fresh";
    public const string Aging = "aging";
    public const string Stale = "stale";

    /// <summary>No snapshot has been stored yet.</summary>
    public const string Missing = "missing";
}
