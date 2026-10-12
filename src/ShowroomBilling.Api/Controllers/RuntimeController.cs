using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Infrastructure.Health;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController(
    IConfiguration configuration,
    IDatabaseConnectionVerifier databaseConnectionVerifier,
    IHostEnvironment hostEnvironment,
    MaintenanceTokenStore maintenanceTokenStore,
    ClientPresenceRegistry clientPresenceRegistry,
    DatabaseHealthProbe databaseHealthProbe,
    DatabaseHealthCache databaseHealthCache,
    ILogger<RuntimeController> logger) : ControllerBase
{
    [HttpPost("database/maintenance/test")]
    public async Task<ActionResult<DatabaseConfigurationTestResponse>> TestDatabaseConfigurationForMaintenance(
        [FromBody] TestDatabaseConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (AuthorizeMaintenance() is { } denied)
        {
            return denied;
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return ControllerProblemDetails.BadRequest("PostgreSQL connection string is required.");
        }

        if (!PostgresConnectionStringNormalizer.TryNormalize(
            request.ConnectionString,
            out var connectionString,
            out var error))
        {
            return BadRequest(new DatabaseConfigurationTestResponse(false, error));
        }

        var verification = await databaseConnectionVerifier.VerifyAsync(
            connectionString,
            ExpectedDatabaseIdentity(hostEnvironment.EnvironmentName),
            cancellationToken);
        return Ok(new DatabaseConfigurationTestResponse(verification.Success, verification.Message));
    }

    [HttpPut("database/maintenance")]
    public async Task<ActionResult<DatabaseConfigurationResponse>> UpdateDatabaseConfigurationForMaintenance(
        [FromBody] UpdateDatabaseConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (AuthorizeMaintenance() is { } denied)
        {
            return denied;
        }

        return await VerifyAndSaveAsync(request, cancellationToken);
    }

    [HttpGet("health")]
    public async Task<ActionResult<RuntimeHealthResponse>> GetHealth(CancellationToken cancellationToken = default)
    {
        if (!databaseHealthCache.TryGet(out var database))
        {
            (database, var check) = await databaseHealthProbe.CheckAsync(cancellationToken);
            // A caller that gave up mid-check makes the check itself fail,
            // which says nothing about the database: do not hand that on.
            if (!cancellationToken.IsCancellationRequested)
            {
                LogIfChanged(databaseHealthCache.Set(database), database, check);
            }
        }

        var expectedDatabaseIdentity = ExpectedDatabaseIdentity(hostEnvironment.EnvironmentName);
        bool? databaseIdentityMatches = database.Reachable
            ? DatabaseConnectionVerifier.DatabaseIdentityMatches(database.Identity, expectedDatabaseIdentity)
            : null;
        var schemaBehind = database.PendingMigrationCount > 0;

        var message = databaseIdentityMatches == false
            ? $"Database identity mismatch: PostgreSQL is reachable, but database identity is {database.Identity ?? "unavailable"}; expected {expectedDatabaseIdentity} for {hostEnvironment.EnvironmentName}."
            : schemaBehind
                ? $"Database upgrade needed: PostgreSQL is reachable ({database.Identity}), but {database.PendingMigrationCount} schema migration(s) this version needs have not been applied. Bills and invoice numbers cannot be read or saved until the database owner applies them."
                : database.Reachable
                    ? $"API is online and PostgreSQL is reachable ({database.Identity})."
                    : "API is online. PostgreSQL must be configured and reachable for readiness.";

        var response = new RuntimeHealthResponse(
            (schemaBehind || !database.Reachable ? HealthStatus.Unhealthy : HealthStatus.Healthy).ToString(),
            ApiAvailable: true,
            DatabaseConfigured: !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")),
            DatabaseReachable: database.Reachable,
            SettingsLoadedFromApi: true,
            Message: message,
            DatabaseIdentity: database.Identity,
            ExpectedDatabaseIdentity: expectedDatabaseIdentity,
            DatabaseIdentityMatches: databaseIdentityMatches,
            ActiveClientCount: clientPresenceRegistry.ActiveCount,
            PendingMigrationCount: database.PendingMigrationCount);

        return Ok(response);
    }

    /// <summary>One line when the database stops answering and one when it answers again.</summary>
    private void LogIfChanged(DatabaseHealthReading? before, DatabaseHealthReading now, HealthCheckResult check)
    {
        if (before?.Reachable == now.Reachable)
        {
            return;
        }

        if (!now.Reachable)
        {
            logger.LogWarning(check.Exception, "The database is not answering. {Description}", check.Description);
        }
        else if (before is not null)
        {
            logger.LogWarning("The database is answering again.");
        }
    }

    private DatabaseConfigurationResponse BuildDatabaseConfigurationResponse(
        string connectionString,
        bool requiresRestart)
    {
        return new DatabaseConfigurationResponse(
            Provider: "PostgreSQL",
            MaskedConnectionString: MaskConnectionString(connectionString),
            ConfigPath: DatabaseConfigurationStore.ConfigPathForEnvironment(hostEnvironment.EnvironmentName),
            IsLocalOverridePresent: DatabaseConfigurationStore.ExistsForEnvironment(hostEnvironment.EnvironmentName),
            RequiresApiRestart: requiresRestart,
            EnvironmentName: hostEnvironment.EnvironmentName,
            IsEnvironmentOverridePresent: !string.IsNullOrWhiteSpace(DatabaseConfigurationStore.GetEnvironmentConnectionString()),
            StorageProtection: "Windows DPAPI CurrentUser");
    }

    /// <summary>
    /// The one way a connection string is accepted: it must parse, reach the
    /// database, and be the right database for this environment before it is
    /// written to the protected override file.
    /// </summary>
    private async Task<ActionResult<DatabaseConfigurationResponse>> VerifyAndSaveAsync(
        UpdateDatabaseConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var connectionString = ParseConnectionStringOrProblem(request);
        if (connectionString.Result is not null)
        {
            return connectionString.Result;
        }

        var verification = await databaseConnectionVerifier.VerifyAsync(
            connectionString.Value!,
            ExpectedDatabaseIdentity(hostEnvironment.EnvironmentName),
            cancellationToken);
        if (!verification.Success)
        {
            return ControllerProblemDetails.BadRequest(verification.Message);
        }

        await DatabaseConfigurationStore.SavePostgresConnectionStringAsync(
            connectionString.Value!,
            hostEnvironment.EnvironmentName,
            cancellationToken);

        return Ok(BuildDatabaseConfigurationResponse(connectionString.Value!, requiresRestart: true));
    }

    private ActionResult<string> ParseConnectionStringOrProblem(UpdateDatabaseConfigurationRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return ControllerProblemDetails.BadRequest("PostgreSQL connection string is required.");
        }

        if (!PostgresConnectionStringNormalizer.TryNormalize(
            request.ConnectionString,
            out var connectionString,
            out var error))
        {
            return ControllerProblemDetails.BadRequest(error);
        }

        return connectionString;
    }

    private ActionResult? AuthorizeMaintenance()
    {
        if (ServerRequestGuard.RequireLoopback(HttpContext) is { } denied)
        {
            return denied;
        }

        if (!Request.Headers.TryGetValue(MaintenanceTokenConstants.HeaderName, out var headerValues)
            || !maintenanceTokenStore.Validate(headerValues.ToString()))
        {
            return ControllerProblemDetails.Unauthorized(
                "Maintenance token invalid.", "Server maintenance actions require the local maintenance token.");
        }

        return null;
    }

    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "***";
            }
            return builder.ConnectionString;
        }
        catch
        {
            return "<invalid connection string>";
        }
    }

    private static string ExpectedDatabaseIdentity(string environmentName) =>
        environmentName.Trim().Equals("Development", StringComparison.OrdinalIgnoreCase)
            ? "DEV"
            : environmentName.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? "PROD"
                : environmentName.Trim().ToUpperInvariant();
}
