using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Contracts.Health;

/// <summary>The answer of <c>GET /api/health</c>: what a counter or the server tray asks on every poll.</summary>
/// <param name="Database">Whether the database answers, which one it is, and whether it has had this build's migrations.</param>
/// <param name="Masters">How old the stored Tally lists are. Null when they were not asked for or could not be read.</param>
public sealed record HealthResponse(
    RuntimeHealthResponse Database,
    MasterFreshnessSummaryResponse? Masters = null);
