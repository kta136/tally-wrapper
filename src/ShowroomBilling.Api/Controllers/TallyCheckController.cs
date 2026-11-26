using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;

namespace ShowroomBilling.Api.Controllers;

/// <summary>
/// Settings → Tally check. A read: it asks Tally for the year's sales vouchers
/// and compares them with the bills, changing nothing, so it is open to every
/// counter like the other reads. It is run only when an operator asks.
/// </summary>
[ApiController]
[Route("api/tally-check")]
public sealed class TallyCheckController(TallyComparison comparison) : ControllerBase
{
    [HttpGet("compare")]
    public async Task<ActionResult<TallyComparisonResponse>> Compare(
        [FromQuery] string? fiscalYear,
        CancellationToken cancellationToken)
        => Ok(await comparison.CompareAsync(fiscalYear, cancellationToken));
}
