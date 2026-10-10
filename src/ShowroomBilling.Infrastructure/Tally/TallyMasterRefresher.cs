using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public Task<TallyMasterRefreshResult> RefreshCompaniesAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(
            MasterTypes.Companies,
            source.FetchCompaniesAsync,
            (items, token) => snapshotService.IngestCompaniesAsync(
                new PushCompanySnapshotRequest(Showrooms.DefaultCode, DateTimeOffset.UtcNow, items), token),
            cancellationToken);

    public Task<TallyMasterRefreshResult> RefreshLedgersAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(
            MasterTypes.Ledgers,
            source.FetchLedgersAsync,
            (items, token) => snapshotService.IngestLedgersAsync(
                new PushLedgerSnapshotRequest(Showrooms.DefaultCode, DateTimeOffset.UtcNow, items), token),
            cancellationToken);

    public Task<TallyMasterRefreshResult> RefreshStockItemsAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(
            MasterTypes.StockItems,
            source.FetchStockItemsAsync,
            (items, token) => snapshotService.IngestStockItemsAsync(
                new PushStockItemSnapshotRequest(Showrooms.DefaultCode, DateTimeOffset.UtcNow, items), token),
            cancellationToken);

    public Task<TallyMasterRefreshResult> RefreshVoucherTypesAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(
            MasterTypes.VoucherTypes,
            source.FetchVoucherTypesAsync,
            (items, token) => snapshotService.IngestVoucherTypesAsync(
                new PushVoucherTypeSnapshotRequest(Showrooms.DefaultCode, DateTimeOffset.UtcNow, items), token),
            cancellationToken);

    /// <summary>
    /// Fetches one master from Tally and stores it. Never throws for a Tally
    /// or database problem: the result row carries the reason, so a refresh of
    /// all four reports each master separately.
    /// </summary>
    private async Task<TallyMasterRefreshResult> RefreshAsync<TItem>(
        string masterType,
        Func<CancellationToken, Task<IReadOnlyList<TItem>>> fetch,
        Func<IReadOnlyList<TItem>, CancellationToken, Task<MasterSnapshotAcceptedResponse>> ingest,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await fetch(cancellationToken);
            var response = await ingest(items, cancellationToken);
            return new TallyMasterRefreshResult(masterType, true, response.ItemCount, response.BatchId, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away; do not report that as a Tally failure.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Refreshing {MasterType} from Tally timed out.", masterType);
            return Fail(masterType, "Tally did not respond before the configured timeout.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Refreshing {MasterType} could not reach Tally.", masterType);
            return Fail(masterType, $"Could not reach Tally. Check that Tally is running and the connection settings are correct. ({ex.Message})");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Refreshing {MasterType} from Tally failed.", masterType);
            return Fail(masterType, ex.Message);
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
