using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Numbering;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/numbering")]
public sealed class NumberingController(NumberingService numberingService) : ControllerBase
{
    [HttpGet("preview")]
    public async Task<ActionResult<NumberingPreviewResponse>> Preview(
        [FromQuery] string? fiscalYear,
        CancellationToken cancellationToken)
    {
        var response = await numberingService.GetPreviewAsync(fiscalYear, cancellationToken);
        return Ok(response);
    }
}
