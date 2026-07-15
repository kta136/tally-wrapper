using ShowroomBilling.Application.Auditing;

namespace ShowroomBilling.Api.Security;

public sealed class HttpAuditActorContext(IHttpContextAccessor httpContextAccessor) : IAuditActorContext
{
    private static readonly AuditActor SystemActor = new("system", null);

    public AuditActor Current
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return SystemActor;
            }

            var adminSessionId = user.FindFirst(AdminAuthenticationHandler.AdminSessionIdClaim)?.Value;
            if (!string.IsNullOrWhiteSpace(adminSessionId))
            {
                var actorLabel = user.FindFirst(AdminAuthenticationHandler.AdminActorLabelClaim)?.Value;
                return new AuditActor("admin", string.IsNullOrWhiteSpace(actorLabel) ? adminSessionId : actorLabel);
            }

            if (user.IsInRole(DevicePolicy.PolicyName))
            {
                return new AuditActor("device", "desktop");
            }

            return SystemActor;
        }
    }
}
