using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class AdminController(
    IAdminAuthService adminService,
    IOptions<DeviceAuthOptions> deviceAuthOptions) : ControllerBase
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
        var status = await adminService.GetPasscodeStatusAsync(cancellationToken);
        if (!status.IsConfigured
            && ServerRequestGuard.RequireLoopbackForServerMode(HttpContext, deviceAuthOptions.Value) is { } denied)
        {
            return denied;
        }

        // Replacing an existing passcode needs an unlocked admin session as
        // well as the current passcode, so the endpoint cannot be used to
        // brute-force and take over admin.
        if (status.IsConfigured
            && !(await HttpContext.AuthenticateAsync(AdminAuthenticationHandler.SchemeName)).Succeeded)
        {
            return ControllerProblemDetails.Forbidden(
                "Admin session required", "Unlock admin before changing the passcode.");
        }

        await adminService.SetPasscodeAsync(request, cancellationToken);
        return NoContent();
    }

    [HttpPost("unlock")]
    public async Task<ActionResult<AdminUnlockResponse>> Unlock(
        [FromBody] AdminUnlockRequest request,
        CancellationToken cancellationToken)
    {
        var response = await adminService.UnlockAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("logout")]
    public async Task<ActionResult> Logout(
        [FromBody] AdminLogoutRequest request,
        CancellationToken cancellationToken)
    {
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
