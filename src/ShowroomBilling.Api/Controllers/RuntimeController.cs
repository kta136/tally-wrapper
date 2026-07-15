using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Options;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController(
    IConfiguration configuration,
    ICloudSettingsService cloudSettingsService,
    ShowroomBillingDbContext dbContext,
    HealthCheckService healthCheckService,
    IDatabaseConnectionVerifier databaseConnectionVerifier,
    AppliedDatabaseConfiguration appliedDatabaseConfiguration,
    IHostEnvironment hostEnvironment,
    IOptions<ApiRuntimeOptions> runtimeOptions,
    IOptions<DeviceAuthOptions> deviceAuthOptions,
    MaintenanceTokenStore maintenanceTokenStore,
    ClientPresenceRegistry clientPresenceRegistry) : ControllerBase
{
    [HttpGet("bootstrap")]
    public async Task<ActionResult<RuntimeBootstrapResponse>> GetBootstrap(CancellationToken cancellationToken)
    {
        var options = runtimeOptions.Value;
        var databaseIdentity = await TryGetDatabaseIdentityAsync(cancellationToken);
        var expectedDatabaseIdentity = ExpectedDatabaseIdentity(hostEnvironment.EnvironmentName);
        var databaseIdentityMatches = DatabaseIdentityMatches(databaseIdentity, expectedDatabaseIdentity);
        var notes = new List<string>
        {
            "Desktop bootstrap is local-only. Shared settings are loaded from the API.",
            "Posting to Tally is synchronous and manual — no queue, no background workers."
        };
        notes.Add(databaseIdentityMatches == true
            ? $"Database identity: {databaseIdentity}."
            : $"Database identity mismatch: database says {databaseIdentity ?? "unavailable"}, API expects {expectedDatabaseIdentity}.");

        try
        {
            var effectiveSettings = await cloudSettingsService.GetEffectiveSettingsAsync(cancellationToken);
            notes.Insert(1, $"Active company: {effectiveSettings.Settings.Connection.ActiveCompanyName}. Tally endpoint: {effectiveSettings.Settings.Connection.Host}:{effectiveSettings.Settings.Connection.Port}.");
        }
        catch
        {
            notes.Insert(1, "Cloud settings are currently unavailable because PostgreSQL is not ready. Desktop should stay in degraded bootstrap mode.");
        }

        var response = new RuntimeBootstrapResponse(
            options.ProductName,
            HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName,
            options.ApiVersion,
            "cloud",
            options.DefaultShowroomName,
            null,
            notes,
            databaseIdentity,
            expectedDatabaseIdentity,
            databaseIdentityMatches);

        return Ok(response);
    }

    [HttpGet("database")]
    public ActionResult<DatabaseConfigurationResponse> GetDatabaseConfiguration()
    {
        if (ServerRequestGuard.RequireLoopbackForServerMode(HttpContext, deviceAuthOptions.Value) is { } denied)
        {
            return denied;
        }

        var connectionString = configuration.GetConnectionString("Postgres") ?? string.Empty;
        return Ok(BuildDatabaseConfigurationResponse(
            connectionString,
            requiresRestart: !IsAppliedConnectionString(connectionString)));
    }

    [HttpPut("database")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<DatabaseConfigurationResponse>> UpdateDatabaseConfiguration(
        [FromBody] UpdateDatabaseConfigurationRequest request,
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

    [HttpPut("database/bootstrap")]
    [AllowAnonymous]
    public async Task<ActionResult<DatabaseConfigurationResponse>> BootstrapDatabaseConfiguration(
        [FromBody] UpdateDatabaseConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (ServerRequestGuard.RequireLoopbackForServerMode(HttpContext, deviceAuthOptions.Value) is { } denied)
        {
            return denied;
        }

        if (!CanBootstrapDatabaseConfiguration())
        {
            return Conflict(new ProblemDetails
            {
                Title = "Database bootstrap is closed.",
                Detail = "A local or environment database override is already configured. Use the admin-protected database update flow.",
                Status = StatusCodes.Status409Conflict
            });
        }

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

    [HttpPost("database/test")]
    public async Task<ActionResult<DatabaseConfigurationTestResponse>> TestDatabaseConfiguration(
        [FromBody] TestDatabaseConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (ServerRequestGuard.RequireLoopbackForServerMode(HttpContext, deviceAuthOptions.Value) is { } denied)
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

    [HttpPost("database/maintenance/test")]
    public async Task<ActionResult<DatabaseConfigurationTestResponse>> TestDatabaseConfigurationForMaintenance(
        [FromBody] TestDatabaseConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        if (AuthorizeMaintenance() is { } denied)
        {
            return denied;
        }

        return await TestDatabaseConfiguration(request, cancellationToken);
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
                ActiveClientCount: activeClientCount));
        }

        var healthReport = await healthCheckService.CheckHealthAsync(_ => true, cancellationToken);
        var postgresReachable = healthReport.Entries.TryGetValue("postgres", out var databaseEntry)
            && databaseEntry.Status == HealthStatus.Healthy;
        var databaseIdentity = postgresReachable
            ? await TryGetDatabaseIdentityAsync(cancellationToken)
            : null;
        bool? databaseIdentityMatches = postgresReachable
            ? DatabaseIdentityMatches(databaseIdentity, expectedDatabaseIdentity)
            : null;
        var databaseReachable = postgresReachable;

        var message = databaseIdentityMatches == false
            ? $"Database identity mismatch: PostgreSQL is reachable, but database identity is {databaseIdentity ?? "unavailable"}; expected {expectedDatabaseIdentity} for {hostEnvironment.EnvironmentName}."
            : databaseReachable
                ? $"API foundation is online and PostgreSQL is reachable ({databaseIdentity})."
                : "API foundation is online. PostgreSQL must be configured and reachable for readiness.";

        var response = new RuntimeHealthResponse(
            healthReport.Status.ToString(),
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
            ActiveClientCount: activeClientCount);

        return Ok(response);
    }

    private DatabaseConfigurationResponse BuildDatabaseConfigurationResponse(
        string connectionString,
        bool requiresRestart)
    {
        return new DatabaseConfigurationResponse(
            Provider: "PostgreSQL",
            ConnectionString: string.Empty,
            MaskedConnectionString: MaskConnectionString(connectionString),
            ConfigPath: DatabaseConfigurationStore.ConfigPathForEnvironment(hostEnvironment.EnvironmentName),
            IsLocalOverridePresent: DatabaseConfigurationStore.ExistsForEnvironment(hostEnvironment.EnvironmentName),
            RequiresApiRestart: requiresRestart,
            EnvironmentName: hostEnvironment.EnvironmentName,
            IsEnvironmentOverridePresent: !string.IsNullOrWhiteSpace(DatabaseConfigurationStore.GetEnvironmentConnectionString()),
            StorageProtection: "Windows DPAPI CurrentUser",
            CanBootstrapWithoutAdmin: CanBootstrapDatabaseConfiguration());
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

    private bool CanBootstrapDatabaseConfiguration() =>
        !DatabaseConfigurationStore.ExistsForEnvironment(hostEnvironment.EnvironmentName)
        && string.IsNullOrWhiteSpace(DatabaseConfigurationStore.GetEnvironmentConnectionString());

    private ActionResult? AuthorizeMaintenance()
    {
        if (ServerRequestGuard.RequireLoopback(HttpContext) is { } denied)
        {
            return denied;
        }

        if (!Request.Headers.TryGetValue(MaintenanceTokenConstants.HeaderName, out var headerValues)
            || !maintenanceTokenStore.Validate(headerValues.ToString()))
        {
            return new ObjectResult(new ProblemDetails
            {
                Title = "Maintenance token invalid.",
                Detail = "Server maintenance actions require the local maintenance token.",
                Status = StatusCodes.Status401Unauthorized
            })
            {
                StatusCode = StatusCodes.Status401Unauthorized
            };
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

    private bool IsAppliedConnectionString(string connectionString) =>
        string.Equals(
            NormalizeConnectionString(connectionString),
            NormalizeConnectionString(appliedDatabaseConfiguration.PostgresConnectionString),
            StringComparison.Ordinal);

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

    private static bool DatabaseIdentityMatches(string? databaseIdentity, string expectedDatabaseIdentity) =>
        !string.IsNullOrWhiteSpace(databaseIdentity)
        && !databaseIdentity.Trim().Equals("UNSET", StringComparison.OrdinalIgnoreCase)
        && databaseIdentity.Trim().Equals(expectedDatabaseIdentity, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeConnectionString(string connectionString)
    {
        return DatabaseConnectionStringConfiguration.NormalizeOrOriginal(connectionString);
    }
}
