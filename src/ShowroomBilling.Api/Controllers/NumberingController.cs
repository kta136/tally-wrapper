using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/numbering")]
public sealed class NumberingController(INumberingService numberingService) : ControllerBase
{
    [HttpGet("preview")]
    public async Task<ActionResult<NumberingPreviewResponse>> Preview(
        [FromQuery] string? documentType,
        [FromQuery] string? fiscalYear,
        CancellationToken cancellationToken)
    {
        var resolvedType = string.IsNullOrWhiteSpace(documentType)
            ? INumberingService.DocumentTypeSalesInvoice
            : documentType;
        var response = await numberingService.GetPreviewAsync(resolvedType, fiscalYear, cancellationToken);
        return Ok(response);
    }
}
