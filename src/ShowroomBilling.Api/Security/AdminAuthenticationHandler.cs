using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Api.Security;

/// <summary>
/// Reads the <c>X-Admin-Token</c> header, validates via <see cref="IAdminAuthService"/>,
/// and builds a ClaimsPrincipal. Also publishes the session on
/// <c>HttpContext.Items[AdminTokenConstants.HttpContextItemKey]</c> so
/// <c>AdminController.GetSession()</c> can keep reading it without a ClaimsPrincipal round-trip.
/// </summary>
public sealed class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAdminAuthService adminService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AdminToken";
    public const string AdminSessionIdClaim = "admin_session_id";
    public const string AdminActorLabelClaim = "admin_actor_label";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AdminTokenConstants.HeaderName, out var headerValues))
        {
            return AuthenticateResult.NoResult();
        }

        var token = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var session = await adminService.ValidateTokenAsync(token, Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("Admin token invalid or expired.");
        }

        Context.Items[AdminTokenConstants.HttpContextItemKey] = session;

        var claims = new List<Claim>
        {
            new(AdminSessionIdClaim, session.SessionId.ToString()),
            new(ClaimTypes.Role, AdminPolicy.PolicyName),
        };
        if (!string.IsNullOrWhiteSpace(session.ActorLabel))
        {
            claims.Add(new Claim(AdminActorLabelClaim, session.ActorLabel));
            claims.Add(new Claim(ClaimTypes.Name, session.ActorLabel));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}

public static class AdminPolicy
{
    public const string PolicyName = "Admin";
}
