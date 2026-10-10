using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Leases;
using ShowroomBilling.Contracts.Leases;

namespace ShowroomBilling.Api.Middleware;

/// <summary>
/// Translates known domain/application exceptions into HTTP status codes +
/// RFC 7807 ProblemDetails. Unknown exceptions fall through to the default
/// handler (500).
/// </summary>
public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Preserve the richer DraftLeaseConflictResponse body the Desktop client
        // already reads on 409s. Do NOT fold this one into ProblemDetails.
        if (exception is DraftLeaseConflictException draftConflict)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            await httpContext.Response.WriteAsJsonAsync(
                new DraftLeaseConflictResponse(draftConflict.Message, draftConflict.ExistingLease),
                cancellationToken);
            return true;
        }

        var (status, title) = MapException(exception);
        if (status is null)
        {
            return false;
        }

        // Standards-compliant Retry-After hint for throttled callers.
        if (exception is AdminUnlockThrottledException throttled)
        {
            httpContext.Response.Headers["Retry-After"] =
                ((int)Math.Ceiling(throttled.RetryAfter.TotalSeconds)).ToString();
        }

        httpContext.Response.StatusCode = status.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = DetailFor(exception)
            }
        });
    }

    private static string DetailFor(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException =>
            "This record was changed by another counter or request while you were working on it. Reload it and try again.",
        // .NET appends " (Parameter 'x')" to the message of an ArgumentException
        // that names a parameter. The operator reads this text; strip it.
        ArgumentException { ParamName: { Length: > 0 } paramName } =>
            exception.Message.Replace($" (Parameter '{paramName}')", string.Empty, StringComparison.Ordinal),
        _ => exception.Message
    };

    private static (int? Status, string Title) MapException(Exception exception) => exception switch
    {
        BillNotFoundException      => (StatusCodes.Status404NotFound,   "Bill not found"),
        DraftLeaseNotFoundException => (StatusCodes.Status404NotFound,   "Draft lease not found"),
        TallyPreflightUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Tally unavailable"),
        BillStateConflictException => (StatusCodes.Status409Conflict,   "Bill state conflict"),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Concurrent change"),
        BillValidationException    => (StatusCodes.Status400BadRequest, "Bill payload invalid"),
        AdminPasscodeNotConfiguredException => (StatusCodes.Status409Conflict, "Admin passcode not configured"),
        DraftLeaseOwnershipException => (StatusCodes.Status403Forbidden, "Draft lease ownership required"),
        AdminPasscodeInvalidException => (StatusCodes.Status401Unauthorized, "Admin passcode invalid"),
        AdminUnlockThrottledException => (StatusCodes.Status429TooManyRequests, "Admin unlock throttled"),
        RequestValidationException => (StatusCodes.Status400BadRequest, "Bad request"),
        // Guard clauses and not-yet-converted input checks. Still a client error.
        ArgumentException => (StatusCodes.Status400BadRequest, "Bad request"),
        _ => ((int?)null, string.Empty)
    };
}
