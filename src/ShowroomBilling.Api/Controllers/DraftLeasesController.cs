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
    [HttpPost("acquire")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<DraftLeaseAcquireResult>> Acquire(
        [FromBody] DraftLeaseAcquireRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        var result = await leaseService.AcquireAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{leaseId:guid}/renew")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<DraftLeaseResponse>> Renew(
        Guid leaseId,
        [FromBody] DraftLeaseRenewRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        var result = await leaseService.RenewAsync(leaseId, request, cancellationToken);
        return Ok(result);
    }

    [HttpPost("{leaseId:guid}/release")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<DraftLeaseResponse>> Release(
        Guid leaseId,
        [FromBody] DraftLeaseReleaseRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        var result = await leaseService.ReleaseAsync(leaseId, request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("bill/{billId:guid}")]
    public async Task<ActionResult<DraftLeaseResponse>> GetForBill(
        Guid billId,
        CancellationToken cancellationToken)
    {
        var lease = await leaseService.GetActiveForBillAsync(billId, cancellationToken);
        return lease is null ? NoContent() : Ok(lease);
    }

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
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        var result = await leaseService.ForceReleaseAsync(leaseId, request, cancellationToken);
        return Ok(result);
    }
}
