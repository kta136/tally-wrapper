using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Api.Security;

/// <summary>
/// Reads the <c>X-Device-Token</c> header and validates it against the
/// locally-stored secret. This gates write routes against other local
/// processes — the Desktop, running as the same Windows user, is the only
/// process that can read the token file. Admin routes keep their stronger
/// <see cref="AdminAuthenticationHandler"/>-backed policy; this handler is
/// the device-authentication layer only.
/// </summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DeviceTokenStore tokenStore,
    IOptionsMonitor<DeviceAuthOptions> deviceAuthOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = deviceAuthOptions.CurrentValue;
        if (configured.IsTrustedLan
            && TrustedNetworkMatcher.IsTrusted(Request.HttpContext.Connection.RemoteIpAddress, configured))
        {
            return Task.FromResult(AuthenticateResult.Success(BuildTicket()));
        }

        if (!Request.Headers.TryGetValue(DeviceTokenConstants.HeaderName, out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = headerValues.ToString();
        if (!tokenStore.Validate(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("Device token missing or invalid."));
        }

        return Task.FromResult(AuthenticateResult.Success(BuildTicket()));
    }

    private static AuthenticationTicket BuildTicket()
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, DevicePolicy.PolicyName) },
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, SchemeName);
    }
}

public static class DevicePolicy
{
    public const string PolicyName = "Device";
}
