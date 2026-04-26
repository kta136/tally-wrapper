using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
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
        try
        {
            var response = await numberingService.GetPreviewAsync(resolvedType, fiscalYear, cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return ControllerProblemDetails.BadRequest(ex.Message);
        }
    }

    [HttpGet("scopes")]
    public async Task<ActionResult<NumberingScopesResponse>> Scopes(CancellationToken cancellationToken)
    {
        var response = await numberingService.GetScopesAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPost("reserve")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<ReserveNumberResponse>> Reserve(
        [FromBody] ReserveNumberRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        try
        {
            var response = await numberingService.ReserveAsync(request, cancellationToken);
            return response.AlreadyExisted ? Ok(response) : Created(string.Empty, response);
        }
        catch (ArgumentException ex)
        {
            return ControllerProblemDetails.BadRequest(ex.Message);
        }
    }
}
