using Microsoft.AspNetCore.Mvc;
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

        return new ObjectResult(new ProblemDetails
        {
            Title = "Local server setup required.",
            Detail = "This setup or maintenance action is only allowed from the Tally server itself.",
            Status = StatusCodes.Status403Forbidden
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    public static ActionResult? RequireLoopback(HttpContext context)
    {
        if (IsLoopback(context))
        {
            return null;
        }

        return new ObjectResult(new ProblemDetails
        {
            Title = "Local access required.",
            Detail = "This endpoint is only available on the server machine.",
            Status = StatusCodes.Status403Forbidden
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
