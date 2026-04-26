using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.PrintAssets;
using ShowroomBilling.Contracts.PrintAssets;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/print-assets")]
public sealed class PrintAssetsController(IPrintAssetService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PrintAssetListResponse>> List(CancellationToken cancellationToken)
    {
        var response = await service.ListAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPost]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<PrintAssetResponse>> Upload(
        [FromBody] PrintAssetUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        try
        {
            var response = await service.UploadAsync(request, cancellationToken);
            return Created($"/api/print-assets/{response.Id}", response);
        }
        catch (ArgumentException ex)
        {
            return ControllerProblemDetails.BadRequest(ex.Message);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }
        var (metadata, bytes) = result.Value;
        return File(bytes, metadata.ContentType, metadata.FileName);
    }

    [HttpGet("{id:guid}/metadata")]
    public async Task<ActionResult<PrintAssetResponse>> GetMetadata(Guid id, CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result.Value.Metadata);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
