using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Infrastructure.Health;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController(
    IConfiguration configuration,
    ShowroomBillingDbContext dbContext,
    HealthCheckService healthCheckService,
    IDatabaseConnectionVerifier databaseConnectionVerifier,
    IHostEnvironment hostEnvironment,
    MaintenanceTokenStore maintenanceTokenStore,
    ClientPresenceRegistry clientPresenceRegistry,
    IDatabaseSchemaState schemaState) : ControllerBase
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
    public async Task<ActionResult<RuntimeHealthResponse>> GetHealth(
        [FromQuery] bool forceDatabase = false,
        CancellationToken cancellationToken = default)
    {
        var activeClientCount = clientPresenceRegistry.ActiveCount;
        var databaseConfigured = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres"));
        var expectedDatabaseIdentity = ExpectedDatabaseIdentity(hostEnvironment.EnvironmentName);
        if (!forceDatabase)
        {
            const string reason = "PostgreSQL health check skipped for cheap background health probe.";
            return Ok(new RuntimeHealthResponse(
                Status: "Skipped",
                ApiAvailable: true,
                DatabaseConfigured: databaseConfigured,
                DatabaseReachable: false,
                SettingsLoadedFromApi: false,
                Message: activeClientCount == 0
                    ? $"{reason} No active billing clients are registered."
                    : $"{reason} Active clients: {activeClientCount}.",
                DatabaseIdentity: null,
                ExpectedDatabaseIdentity: expectedDatabaseIdentity,
                DatabaseIdentityMatches: null,
                DatabaseHealthSkipped: true,
                DatabaseHealthSkipReason: reason,
                ActiveClientCount: activeClientCount,
                PendingMigrationCount: schemaState.PendingMigrationCount));
        }

        var healthReport = await healthCheckService.CheckHealthAsync(_ => true, cancellationToken);
        var postgresReachable = healthReport.Entries.TryGetValue("postgres", out var databaseEntry)
            && databaseEntry.Status == HealthStatus.Healthy;
        var databaseIdentity = postgresReachable
            ? await TryGetDatabaseIdentityAsync(cancellationToken)
            : null;
        bool? databaseIdentityMatches = postgresReachable
            ? DatabaseConnectionVerifier.DatabaseIdentityMatches(databaseIdentity, expectedDatabaseIdentity)
            : null;
        var databaseReachable = postgresReachable;
        var pendingMigrations = postgresReachable
            ? await TryCountPendingMigrationsAsync(cancellationToken)
            : schemaState.PendingMigrationCount;
        var schemaBehind = pendingMigrations > 0;

        var message = databaseIdentityMatches == false
            ? $"Database identity mismatch: PostgreSQL is reachable, but database identity is {databaseIdentity ?? "unavailable"}; expected {expectedDatabaseIdentity} for {hostEnvironment.EnvironmentName}."
            : schemaBehind
                ? $"Database upgrade needed: PostgreSQL is reachable ({databaseIdentity}), but {pendingMigrations} schema migration(s) this version needs have not been applied. Bills and invoice numbers cannot be read or saved until the database owner applies them."
                : databaseReachable
                    ? $"API is online and PostgreSQL is reachable ({databaseIdentity})."
                    : "API is online. PostgreSQL must be configured and reachable for readiness.";

        var response = new RuntimeHealthResponse(
            schemaBehind ? HealthStatus.Unhealthy.ToString() : healthReport.Status.ToString(),
            ApiAvailable: true,
            DatabaseConfigured: databaseConfigured,
            DatabaseReachable: databaseReachable,
            SettingsLoadedFromApi: true,
            Message: message,
            DatabaseIdentity: databaseIdentity,
            ExpectedDatabaseIdentity: expectedDatabaseIdentity,
            DatabaseIdentityMatches: databaseIdentityMatches,
            DatabaseHealthSkipped: false,
            DatabaseHealthSkipReason: null,
            ActiveClientCount: activeClientCount,
            PendingMigrationCount: pendingMigrations);

        return Ok(response);
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

    /// <summary>The last known count if the database cannot be asked right now.</summary>
    private async Task<int?> TryCountPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await DatabaseSchemaState.CheckAsync(dbContext, schemaState, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return schemaState.PendingMigrationCount;
        }
    }

    private async Task<string?> TryGetDatabaseIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.DatabaseIdentity
                .AsNoTracking()
                .Where(x => x.Key == "environment")
                .Select(x => x.Value)
                .SingleOrDefaultAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static string ExpectedDatabaseIdentity(string environmentName) =>
        environmentName.Trim().Equals("Development", StringComparison.OrdinalIgnoreCase)
            ? "DEV"
            : environmentName.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase)
                ? "PROD"
                : environmentName.Trim().ToUpperInvariant();
}
