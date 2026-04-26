using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Infrastructure.Tally;

public sealed class TallyMasterRefresher(
    ITallyMasterSource source,
    IMasterSnapshotService snapshotService,
    IServiceScopeFactory scopeFactory,
    ILogger<TallyMasterRefresher> logger) : ITallyMasterRefresher
{
    private const string ShowroomCode = "default";

    public async Task<TallyMasterRefreshResult> RefreshCompaniesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await source.FetchCompaniesAsync(cancellationToken);
            if (items is null)
            {
                return Fail("companies", "Tally did not return a response. Check connection settings and that Tally is running.");
            }
            var response = await snapshotService.IngestCompaniesAsync(
                new PushCompanySnapshotRequest(ShowroomCode, DateTimeOffset.UtcNow, items),
                cancellationToken);
            return new TallyMasterRefreshResult("companies", true, response.ItemCount, response.BatchId, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RefreshCompaniesAsync failed.");
            return Fail("companies", ex.Message);
        }
    }

    public async Task<TallyMasterRefreshResult> RefreshLedgersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await source.FetchLedgersAsync(cancellationToken);
            if (items is null)
            {
                return Fail("ledgers", "Tally did not return a response.");
            }
            var response = await snapshotService.IngestLedgersAsync(
                new PushLedgerSnapshotRequest(ShowroomCode, DateTimeOffset.UtcNow, items),
                cancellationToken);
            return new TallyMasterRefreshResult("ledgers", true, response.ItemCount, response.BatchId, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RefreshLedgersAsync failed.");
            return Fail("ledgers", ex.Message);
        }
    }

    public async Task<TallyMasterRefreshResult> RefreshStockItemsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await source.FetchStockItemsAsync(cancellationToken);
            if (items is null)
            {
                return Fail("stock-items", "Tally did not return a response.");
            }
            var response = await snapshotService.IngestStockItemsAsync(
                new PushStockItemSnapshotRequest(ShowroomCode, DateTimeOffset.UtcNow, items),
                cancellationToken);
            return new TallyMasterRefreshResult("stock-items", true, response.ItemCount, response.BatchId, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RefreshStockItemsAsync failed.");
            return Fail("stock-items", ex.Message);
        }
    }

    public async Task<TallyMasterRefreshResult> RefreshVoucherTypesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var items = await source.FetchVoucherTypesAsync(cancellationToken);
            if (items is null)
            {
                return Fail("voucher-types", "Tally did not return a response.");
            }
            var response = await snapshotService.IngestVoucherTypesAsync(
                new PushVoucherTypeSnapshotRequest(ShowroomCode, DateTimeOffset.UtcNow, items),
                cancellationToken);
            return new TallyMasterRefreshResult("voucher-types", true, response.ItemCount, response.BatchId, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RefreshVoucherTypesAsync failed.");
            return Fail("voucher-types", ex.Message);
        }
    }

    public async Task<IReadOnlyList<TallyMasterRefreshResult>> RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        // Sequential, not parallel. TallyPrime's local XML endpoint is fragile
        // under concurrent reads and tends to amplify timeouts when fanned out.
        // Each call is wrapped in its own DI scope (separate DbContext) so a
        // mid-call failure on one master doesn't poison the others.
        var refreshes = new Func<ITallyMasterRefresher, CancellationToken, Task<TallyMasterRefreshResult>>[]
        {
            (refresher, token) => refresher.RefreshCompaniesAsync(token),
            (refresher, token) => refresher.RefreshLedgersAsync(token),
            (refresher, token) => refresher.RefreshStockItemsAsync(token),
            (refresher, token) => refresher.RefreshVoucherTypesAsync(token),
        };

        var results = new List<TallyMasterRefreshResult>(refreshes.Length);
        foreach (var refresh in refreshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RefreshInIsolatedScopeAsync(refresh, cancellationToken));
        }
        return results;
    }

    private async Task<TallyMasterRefreshResult> RefreshInIsolatedScopeAsync(
        Func<ITallyMasterRefresher, CancellationToken, Task<TallyMasterRefreshResult>> refresh,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ITallyMasterRefresher>();
        return await refresh(refresher, cancellationToken);
    }

    private static TallyMasterRefreshResult Fail(string masterType, string message) =>
        new(masterType, false, 0, null, message);
}
