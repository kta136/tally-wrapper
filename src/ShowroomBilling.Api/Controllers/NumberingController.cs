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
        [FromQuery] string? documentType,
        [FromQuery] string? fiscalYear,
        CancellationToken cancellationToken)
    {
        var resolvedType = string.IsNullOrWhiteSpace(documentType)
            ? NumberingService.DocumentTypeSalesInvoice
            : documentType;
        var response = await numberingService.GetPreviewAsync(resolvedType, fiscalYear, cancellationToken);
        return Ok(response);
    }
}
