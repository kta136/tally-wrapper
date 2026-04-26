using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Infrastructure.Health;

public sealed class TallyCompanyHealthService(
    ICloudSettingsService cloudSettings,
    ITallyMasterSource tallyMasterSource,
    ILogger<TallyCompanyHealthService> logger) : ITallyCompanyHealthService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    public async Task<TallyCompanyHealthResponse> CheckAsync(CancellationToken cancellationToken = default)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        string? activeCompany;
        try
        {
            var settings = await cloudSettings.GetEffectiveSettingsAsync(cts.Token);
            activeCompany = Normalize(settings.Settings.Connection.ActiveCompanyName);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unhealthy(null, checkedAtUtc, "Tally company health check timed out while loading settings.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Tally company health check could not load cloud settings.");
            return Unhealthy(null, checkedAtUtc, "Cloud settings are unavailable; cannot determine the active Tally company.");
        }

        if (string.IsNullOrWhiteSpace(activeCompany))
        {
            return new TallyCompanyHealthResponse(
                Status: "warning",
                TallyReachable: false,
                ActiveCompanyName: null,
                ActiveCompanyOpen: false,
                CompanyCount: 0,
                CheckedAtUtc: checkedAtUtc,
                Message: "Active Tally company is not configured.");
        }

        IReadOnlyList<CompanySnapshotItem>? companies;
        try
        {
            companies = await tallyMasterSource.FetchCompaniesAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unhealthy(activeCompany, checkedAtUtc, "Tally company health check timed out.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Tally company health check failed.");
            return Unhealthy(activeCompany, checkedAtUtc, "Tally is unreachable. Check that Tally is running and the connection settings are correct.");
        }

        if (companies is null)
        {
            return Unhealthy(activeCompany, checkedAtUtc, "Tally is unreachable. Check that Tally is running and the connection settings are correct.");
        }

        var companyCount = companies.Count;
        var companyOpen = companies.Any(company =>
            string.Equals(Normalize(company.Name), activeCompany, StringComparison.OrdinalIgnoreCase));

        if (companyOpen)
        {
            return new TallyCompanyHealthResponse(
                Status: "healthy",
                TallyReachable: true,
                ActiveCompanyName: activeCompany,
                ActiveCompanyOpen: true,
                CompanyCount: companyCount,
                CheckedAtUtc: checkedAtUtc,
                Message: $"Tally OK - active company '{activeCompany}' is open.");
        }

        return new TallyCompanyHealthResponse(
            Status: "warning",
            TallyReachable: true,
            ActiveCompanyName: activeCompany,
            ActiveCompanyOpen: false,
            CompanyCount: companyCount,
            CheckedAtUtc: checkedAtUtc,
            Message: $"Tally answered, but active company '{activeCompany}' is not open.");
    }

    private static TallyCompanyHealthResponse Unhealthy(string? activeCompany, DateTimeOffset checkedAtUtc, string message) =>
        new(
            Status: "unhealthy",
            TallyReachable: false,
            ActiveCompanyName: activeCompany,
            ActiveCompanyOpen: false,
            CompanyCount: 0,
            CheckedAtUtc: checkedAtUtc,
            Message: message);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
