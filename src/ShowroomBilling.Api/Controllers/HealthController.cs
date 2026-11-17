using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Infrastructure.Health;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(
    DatabaseHealth databaseHealth,
    IMasterSnapshotService masterSnapshotService,
    ITallyCompanyHealthService tallyCompanyHealthService,
    IHostEnvironment hostEnvironment,
    ILogger<HealthController> logger) : ControllerBase
{
    /// <summary>Is anything listening. Asks nothing of the database or of Tally.</summary>
    [HttpGet("live")]
    public IActionResult Live() => Ok(new
    {
        status = "Healthy",
        service = "Tally Wrapper API",
        utcNow = DateTimeOffset.UtcNow
    });

    /// <summary>
    /// What a counter or the tray asks every few seconds, in one answer: the database (always, a
    /// real check that all callers share for 5 seconds) and how old the stored Tally lists are.
    /// </summary>
    /// <param name="masters">False leaves the lists out, for a caller that has asked recently.</param>
    [HttpGet]
    public async Task<ActionResult<HealthResponse>> Get(
        [FromQuery] bool masters = true,
        CancellationToken cancellationToken = default)
    {
        var database = await DatabaseAsync(cancellationToken);
        // Not asked of a database that is not answering: the question would only wait on it.
        var lists = masters && database.DatabaseReachable ? await TryMastersAsync(cancellationToken) : null;
        return Ok(new HealthResponse(database, lists));
    }

    /// <summary>
    /// Asks Tally which companies are open. Its own request, because it takes as long as Tally
    /// takes, and the database's answer must not wait for that.
    /// </summary>
    [HttpGet("tally-company")]
    public async Task<ActionResult<TallyCompanyHealthResponse>> TallyCompany(CancellationToken cancellationToken)
    {
        var response = await tallyCompanyHealthService.CheckAsync(cancellationToken);
        return Ok(response);
    }

    private async Task<RuntimeHealthResponse> DatabaseAsync(CancellationToken cancellationToken)
    {
        var database = await databaseHealth.GetAsync(cancellationToken);

        var expectedIdentity = DatabaseConnectionVerifier.ExpectedIdentityFor(hostEnvironment.EnvironmentName);
        bool? identityMatches = database.Reachable
            ? DatabaseConnectionVerifier.DatabaseIdentityMatches(database.Identity, expectedIdentity)
            : null;
        var schemaBehind = database.PendingMigrationCount > 0;

        var message = identityMatches == false
            ? $"Database identity mismatch: PostgreSQL is reachable, but database identity is {database.Identity ?? "unavailable"}; expected {expectedIdentity} for {hostEnvironment.EnvironmentName}."
            : schemaBehind
                ? $"Database upgrade needed: PostgreSQL is reachable ({database.Identity}), but {database.PendingMigrationCount} schema migration(s) this version needs have not been applied. Bills and invoice numbers cannot be read or saved until the database owner applies them."
                : database.Reachable
                    ? $"API is online and PostgreSQL is reachable ({database.Identity})."
                    : "API is online. PostgreSQL must be configured and reachable for readiness.";

        return new RuntimeHealthResponse(
            schemaBehind || !database.Reachable ? "Unhealthy" : "Healthy",
            DatabaseReachable: database.Reachable,
            Message: message,
            DatabaseIdentity: database.Identity,
            ExpectedDatabaseIdentity: expectedIdentity,
            DatabaseIdentityMatches: identityMatches,
            PendingMigrationCount: database.PendingMigrationCount);
    }

    /// <summary>Null when the lists cannot be read. The cause goes to the log, not to an unauthenticated caller.</summary>
    private async Task<MasterFreshnessSummaryResponse?> TryMastersAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await masterSnapshotService.GetFreshnessSummaryAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Master freshness summary is unavailable.");
            return null;
        }
    }
}
