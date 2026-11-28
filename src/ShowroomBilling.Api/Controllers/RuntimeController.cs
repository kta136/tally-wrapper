using System.Diagnostics.CodeAnalysis;
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
    [ProducesResponseType<DatabaseConfigurationTestResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult> TestDatabaseConfigurationForMaintenance(
        [FromBody] DatabaseConfigurationRequest request,
        CancellationToken cancellationToken) =>
        VerifyConnectionAsync(request, save: false, cancellationToken);

    [HttpPut("database/maintenance")]
    [ProducesResponseType<DatabaseConfigurationResponse>(StatusCodes.Status200OK)]
    public Task<ActionResult> UpdateDatabaseConfigurationForMaintenance(
        [FromBody] DatabaseConfigurationRequest request,
        CancellationToken cancellationToken) =>
        VerifyConnectionAsync(request, save: true, cancellationToken);

    private async Task<ActionResult> VerifyConnectionAsync(
        DatabaseConfigurationRequest request, bool save, CancellationToken cancellationToken)
    {
        if (AuthorizeMaintenance() is { } denied)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(request?.ConnectionString))
        {
            return ControllerProblemDetails.BadRequest("PostgreSQL connection string is required.");
        }

        if (!TryParseConnection(request.ConnectionString.Trim(), out var candidate, out var error))
        {
            return save
                ? ControllerProblemDetails.BadRequest(error)
                : BadRequest(new DatabaseConfigurationTestResponse(false, error));
        }

        var environment = hostEnvironment.EnvironmentName;
        var verification = await databaseConnectionVerifier.VerifyAsync(
            candidate.ConnectionString, DatabaseConnectionVerifier.ExpectedIdentityFor(environment), cancellationToken);
        if (!save) return Ok(verification);
        if (!verification.Success) return ControllerProblemDetails.BadRequest(verification.Message);

        await DatabaseConfigurationStore.SavePostgresConnectionStringAsync(candidate.ConnectionString, environment, cancellationToken);
        if (!string.IsNullOrEmpty(candidate.Password)) candidate.Password = "***";
        if (!string.IsNullOrEmpty(candidate.SslPassword)) candidate.SslPassword = "***";
        return Ok(new DatabaseConfigurationResponse(candidate.ConnectionString, RequiresApiRestart: true));
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

    private static bool TryParseConnection(
        string text,
        [NotNullWhen(true)] out NpgsqlConnectionStringBuilder? candidate,
        out string error)
    {
        candidate = null;
        // An operator may paste the provider's URL, or a psql command containing it.
        if (text.Contains("postgres://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            error = "A postgres:// URL is not accepted. Enter the connection string as Host=...;Port=...;Database=...;Username=...;Password=...";
            return false;
        }
        try
        {
            candidate = new NpgsqlConnectionStringBuilder(text);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
