using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Contracts.Clients;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/clients")]
public sealed class ClientsController(ClientPresenceRegistry presenceRegistry) : ControllerBase
{
    [HttpPost("heartbeat")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public ActionResult<ClientPresenceResponse> Heartbeat([FromBody] ClientHeartbeatRequest request)
    {
        if (request is null)
        {
            return ControllerProblemDetails.BadRequest("Request body is required.");
        }

        var remoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return Ok(presenceRegistry.Register(request, remoteAddress));
    }

    [HttpGet("presence")]
    public ActionResult<ClientPresenceListResponse> Presence()
    {
        if (ServerRequestGuard.RequireLoopback(HttpContext) is { } denied)
        {
            return denied;
        }

        return Ok(presenceRegistry.Snapshot());
    }
}
