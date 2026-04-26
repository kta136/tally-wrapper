using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class AdminController(IAdminAuthService adminService) : ControllerBase
{
    [HttpGet("passcode")]
    public async Task<ActionResult<AdminPasscodeStatusResponse>> GetPasscodeStatus(CancellationToken cancellationToken)
    {
        var response = await adminService.GetPasscodeStatusAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPost("passcode")]
    public async Task<ActionResult> SetPasscode(
        [FromBody] AdminSetPasscodeRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        await adminService.SetPasscodeAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("unlock")]
    public async Task<ActionResult<AdminUnlockResponse>> Unlock(
        [FromBody] AdminUnlockRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        var response = await adminService.UnlockAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("logout")]
    public async Task<ActionResult> Logout(
        [FromBody] AdminLogoutRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }
        await adminService.LogoutAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpGet("session")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public ActionResult<AdminSessionInfoResponse> GetSession()
    {
        var session = HttpContext.Items[ShowroomBilling.Api.Security.AdminTokenConstants.HttpContextItemKey] as AdminSessionInfoResponse;
        return session is null ? Unauthorized() : Ok(session);
    }
}
