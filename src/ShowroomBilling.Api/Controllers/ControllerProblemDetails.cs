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
}
