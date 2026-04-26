using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Application.Tally;

/// <summary>
/// Synchronous "fetch fresh master data from Tally now" abstraction. Invoked only by operator
/// action (Settings → Refresh from Tally, System Health → Refresh all from Tally). There is no
/// background polling — if the operator doesn't click, nothing happens.
///
/// Each call fetches from Tally via XML, writes the snapshot to the DB via IMasterSnapshotService,
/// and returns a brief summary.
/// </summary>
public interface ITallyMasterRefresher
{
    Task<TallyMasterRefreshResult> RefreshCompaniesAsync(CancellationToken cancellationToken = default);
    Task<TallyMasterRefreshResult> RefreshLedgersAsync(CancellationToken cancellationToken = default);
    Task<TallyMasterRefreshResult> RefreshStockItemsAsync(CancellationToken cancellationToken = default);
    Task<TallyMasterRefreshResult> RefreshVoucherTypesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TallyMasterRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default);
}

public sealed record TallyMasterRefreshResult(
    string MasterType,
    bool Succeeded,
    int ItemCount,
    string? BatchId,
    string? ErrorMessage);
