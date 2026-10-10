using Microsoft.AspNetCore.Mvc;

namespace ShowroomBilling.Api.Controllers;

internal static class ControllerProblemDetails
{
    public static BadRequestObjectResult BadRequest(string detail) =>
        new(new ProblemDetails
        {
            Title = "Bad Request",
            Detail = detail,
            Status = StatusCodes.Status400BadRequest
        });

    public static ObjectResult Unauthorized(string title, string detail) =>
        Problem(StatusCodes.Status401Unauthorized, title, detail);

    public static ObjectResult Forbidden(string title, string detail) =>
        Problem(StatusCodes.Status403Forbidden, title, detail);

    private static ObjectResult Problem(int status, string title, string detail) =>
        new(new ProblemDetails { Title = title, Detail = detail, Status = status }) { StatusCode = status };
}
