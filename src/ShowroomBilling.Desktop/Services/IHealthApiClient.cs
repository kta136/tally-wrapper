using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Desktop.Services;

public interface IHealthApiClient
{
    Task<SystemHealthSnapshot> GetSnapshotAsync(
        bool includeTallyCompany,
        bool includeMasterFreshness = true,
        bool forceDatabaseHealth = false,
        CancellationToken cancellationToken = default);
}

public sealed record SystemHealthSnapshot(
    bool ApiReachable,
    MasterFreshnessSummaryResponse? Masters,
    TallyCompanyHealthResponse? TallyCompany,
    RuntimeHealthResponse? Runtime = null)
{
    public string MastersFreshness => Masters?.OverallStatus ?? "unknown";

    public static SystemHealthSnapshot Unreachable() => new(false, null, null, null);
}
