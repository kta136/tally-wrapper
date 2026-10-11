using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/masters")]
public sealed class MastersController(
    IMasterSnapshotService masterSnapshotService,
    ITallyMasterRefresher tallyMasterRefresher) : ControllerBase
{
    // Each read returns the whole current snapshot, in name order.

    [HttpGet(MasterTypes.Companies)]
    public async Task<ActionResult<CompanySnapshotResponse>> GetCompanies(CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetCompaniesAsync(cancellationToken));

    [HttpGet(MasterTypes.Ledgers)]
    public async Task<ActionResult<LedgerSnapshotResponse>> GetLedgers(CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetLedgersAsync(cancellationToken));

    [HttpGet(MasterTypes.StockItems)]
    public async Task<ActionResult<StockItemSnapshotResponse>> GetStockItems(CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetStockItemsAsync(cancellationToken));

    [HttpGet(MasterTypes.VoucherTypes)]
    public async Task<ActionResult<VoucherTypeSnapshotResponse>> GetVoucherTypes(CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetVoucherTypesAsync(cancellationToken));

    /// <summary>
    /// Synchronously fetches master data from Tally and writes the snapshot. This is the
    /// ONLY path by which Tally master data gets updated — no background worker. MasterType
    /// selects which master to fetch; null/empty refreshes all four.
    /// </summary>
    [HttpPost("refresh")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<IReadOnlyList<TallyMasterRefreshResult>>> Refresh(
        [FromBody] MasterRefreshRequest? request,
        CancellationToken cancellationToken)
    {
        var masterType = request?.MasterType?.Trim().ToLowerInvariant();
        IReadOnlyList<TallyMasterRefreshResult> results = masterType switch
        {
            null or "" => await tallyMasterRefresher.RefreshAllAsync(cancellationToken),
            MasterTypes.Companies => [await tallyMasterRefresher.RefreshCompaniesAsync(cancellationToken)],
            MasterTypes.Ledgers => [await tallyMasterRefresher.RefreshLedgersAsync(cancellationToken)],
            MasterTypes.StockItems or MasterTypes.StockItemsSnapshot
                => [await tallyMasterRefresher.RefreshStockItemsAsync(cancellationToken)],
            MasterTypes.VoucherTypes or MasterTypes.VoucherTypesSnapshot
                => [await tallyMasterRefresher.RefreshVoucherTypesAsync(cancellationToken)],
            // A typo used to fall through to "refresh everything".
            _ => throw new RequestValidationException($"Unknown master type '{request!.MasterType}'."),
        };
        return Ok(results);
    }
}
