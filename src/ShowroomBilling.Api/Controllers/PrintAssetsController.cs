using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Infrastructure.PrintAssets;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/print-assets")]
public sealed class PrintAssetsController(PrintAssetService service) : ControllerBase
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
        var response = await service.UploadAsync(request, cancellationToken);
        return Created($"/api/print-assets/{response.Id}", response);
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

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
