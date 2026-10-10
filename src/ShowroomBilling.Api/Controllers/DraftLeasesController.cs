using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/leases/drafts")]
public sealed class DraftLeasesController(IDraftLeaseService leaseService) : ControllerBase
{
    [HttpGet("active")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<DraftLeaseListResponse>> ListActive(
        CancellationToken cancellationToken)
    {
        var response = await leaseService.ListActiveAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPost("{leaseId:guid}/force-release")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<DraftLeaseResponse>> ForceRelease(
        Guid leaseId,
        [FromBody] DraftLeaseForceReleaseRequest request,
        CancellationToken cancellationToken)
    {
        var result = await leaseService.ForceReleaseAsync(leaseId, request, cancellationToken);
        return Ok(result);
    }
}
