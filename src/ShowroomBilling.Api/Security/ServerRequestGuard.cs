using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Controllers;
using ShowroomBilling.Api.Options;

namespace ShowroomBilling.Api.Security;

public static class ServerRequestGuard
{
    public static bool IsLoopback(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        return remoteAddress is not null && System.Net.IPAddress.IsLoopback(
            remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress);
    }

    public static ActionResult? RequireLoopbackForServerMode(HttpContext context, DeviceAuthOptions options)
    {
        if (!options.IsTrustedLan || IsLoopback(context))
        {
            return null;
        }

        return ControllerProblemDetails.Forbidden(
            "Local server setup required.",
            "This setup or maintenance action is only allowed from the Tally server itself.");
    }

    public static ActionResult? RequireLoopback(HttpContext context)
    {
        if (IsLoopback(context))
        {
            return null;
        }

        return ControllerProblemDetails.Forbidden(
            "Local access required.", "This endpoint is only available on the server machine.");
    }
}
