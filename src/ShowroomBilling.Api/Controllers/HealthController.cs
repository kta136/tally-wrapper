using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Contracts.Health;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(
    IMasterSnapshotService masterSnapshotService,
    HealthCheckService healthCheckService,
    ITallyCompanyHealthService tallyCompanyHealthService,
    IStartupStatus startupStatus) : ControllerBase
{
    [HttpGet("startup")]
    public ActionResult<StartupStatusResponse> Startup() => Ok(startupStatus.Snapshot());

    [HttpGet("live")]
    public IActionResult Live() => Ok(new
    {
        status = "Healthy",
        service = "Showroom Billing V2 API",
        utcNow = DateTimeOffset.UtcNow
    });

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(_ => true, cancellationToken);
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description
                })
        };
        return report.Status == HealthStatus.Healthy ? Ok(payload) : StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
    }

    [HttpGet("masters")]
    public async Task<IActionResult> Masters(CancellationToken cancellationToken)
    {
        try
        {
            var summary = await masterSnapshotService.GetFreshnessSummaryAsync(cancellationToken);
            return Ok(summary);
        }
        catch (Exception exception)
        {
            return Ok(new
            {
                status = "Unavailable",
                note = "Master freshness reporting is unavailable. PostgreSQL may be unreachable.",
                error = exception.Message
            });
        }
    }

    [HttpGet("tally-company")]
    public async Task<ActionResult<TallyCompanyHealthResponse>> TallyCompany(CancellationToken cancellationToken)
    {
        var response = await tallyCompanyHealthService.CheckAsync(cancellationToken);
        return Ok(response);
    }
}
