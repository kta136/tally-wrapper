using Microsoft.AspNetCore.Mvc;
using Npgsql;
using ShowroomBilling.Api.Clients;
using ShowroomBilling.Api.Configuration;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Contracts.Maintenance;
using ShowroomBilling.Contracts.Runtime;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/runtime")]
public sealed class RuntimeController(
    IDatabaseConnectionVerifier databaseConnectionVerifier,
    IHostEnvironment hostEnvironment,
    MaintenanceTokenStore maintenanceTokenStore) : ControllerBase
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
            DatabaseConnectionVerifier.ExpectedIdentityFor(hostEnvironment.EnvironmentName),
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
            EnvironmentName: hostEnvironment.EnvironmentName);
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
            DatabaseConnectionVerifier.ExpectedIdentityFor(hostEnvironment.EnvironmentName),
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
}
