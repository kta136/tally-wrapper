using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
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
    [HttpGet("companies")]
    public async Task<ActionResult<CompanySnapshotResponse>> GetCompanies(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        [FromQuery] bool? includeRaw,
        CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetCompaniesAsync(cancellationToken, new MasterSnapshotQuery(search, skip, take, includeRaw)));

    [HttpGet("ledgers")]
    public async Task<ActionResult<LedgerSnapshotResponse>> GetLedgers(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        [FromQuery] bool? includeRaw,
        CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetLedgersAsync(cancellationToken, new MasterSnapshotQuery(search, skip, take, includeRaw)));

    [HttpGet("stock-items")]
    public async Task<ActionResult<StockItemSnapshotResponse>> GetStockItems(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        [FromQuery] bool? includeRaw,
        CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetStockItemsAsync(cancellationToken, new MasterSnapshotQuery(search, skip, take, includeRaw)));

    [HttpGet("voucher-types")]
    public async Task<ActionResult<VoucherTypeSnapshotResponse>> GetVoucherTypes(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        [FromQuery] bool? includeRaw,
        CancellationToken cancellationToken)
        => Ok(await masterSnapshotService.GetVoucherTypesAsync(cancellationToken, new MasterSnapshotQuery(search, skip, take, includeRaw)));

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
        var results = masterType switch
        {
            "companies" => new[] { await tallyMasterRefresher.RefreshCompaniesAsync(cancellationToken) },
            "ledgers" => new[] { await tallyMasterRefresher.RefreshLedgersAsync(cancellationToken) },
            "stock-items" or "stock_items" => new[] { await tallyMasterRefresher.RefreshStockItemsAsync(cancellationToken) },
            "voucher-types" or "voucher_types" => new[] { await tallyMasterRefresher.RefreshVoucherTypesAsync(cancellationToken) },
            _ => (IReadOnlyList<TallyMasterRefreshResult>)await tallyMasterRefresher.RefreshAllAsync(cancellationToken),
        };
        return Ok(results);
    }
}
