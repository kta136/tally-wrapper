using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Masters;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

/// <summary>
/// A refresh never throws for a Tally problem: the result row says what went
/// wrong, and says it differently for a timeout, a refused connection and a
/// configuration error. (The source used to swallow all three into "Tally
/// did not return a response".)
/// </summary>
public sealed class TallyMasterRefresherTests
{
    [Fact]
    public async Task A_successful_refresh_reports_the_stored_batch()
    {
        var refresher = Refresher(_ => Task.FromResult<IReadOnlyList<CompanySnapshotItem>>(
            [new CompanySnapshotItem("Acme Jewellers", true)]));

        var result = await refresher.RefreshCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(MasterTypes.Companies, result.MasterType);
        Assert.Equal(1, result.ItemCount);
        Assert.Equal("batch-1", result.BatchId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task A_refused_connection_says_tally_could_not_be_reached()
    {
        var refresher = Refresher(_ => throw new HttpRequestException("No connection could be made"));

        var result = await refresher.RefreshCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.StartsWith("Could not reach Tally.", result.ErrorMessage);
        Assert.Contains("No connection could be made", result.ErrorMessage);
    }

    [Fact]
    public async Task A_tally_timeout_says_so()
    {
        // The client's own time budget expired; the caller did not cancel.
        var refresher = Refresher(_ => throw new OperationCanceledException());

        var result = await refresher.RefreshCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Tally did not respond before the configured timeout.", result.ErrorMessage);
    }

    [Fact]
    public async Task A_configuration_problem_is_reported_in_its_own_words()
    {
        var refresher = Refresher(_ => throw new InvalidOperationException("Tally host is not configured."));

        var result = await refresher.RefreshCompaniesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Tally host is not configured.", result.ErrorMessage);
    }

    [Fact]
    public async Task A_caller_that_went_away_is_not_reported_as_a_tally_failure()
    {
        using var cts = new CancellationTokenSource();
        var refresher = Refresher(token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CompanySnapshotItem>>([]);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresher.RefreshCompaniesAsync(cts.Token));
    }

    private static TallyMasterRefresher Refresher(Func<CancellationToken, Task<IReadOnlyList<CompanySnapshotItem>>> fetchCompanies) =>
        new(
            new FakeSource(fetchCompanies),
            new FakeSnapshotService(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TallyMasterRefresher>.Instance);

    private sealed class FakeSource(Func<CancellationToken, Task<IReadOnlyList<CompanySnapshotItem>>> fetchCompanies)
        : ITallyMasterSource
    {
        public Task<IReadOnlyList<CompanySnapshotItem>> FetchCompaniesAsync(CancellationToken cancellationToken = default) =>
            fetchCompanies(cancellationToken);

        public Task<IReadOnlyList<LedgerSnapshotItem>> FetchLedgersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StockItemSnapshotItem>> FetchStockItemsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VoucherTypeSnapshotItem>> FetchVoucherTypesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSnapshotService : IMasterSnapshotService
    {
        public Task<MasterSnapshotAcceptedResponse> IngestCompaniesAsync(
            PushCompanySnapshotRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MasterSnapshotAcceptedResponse(
                MasterTypes.Companies,
                "batch-1",
                request.Companies.Count,
                request.FetchedAtUtc,
                DateTimeOffset.UtcNow,
                "stored"));

        public Task<MasterSnapshotAcceptedResponse> IngestLedgersAsync(PushLedgerSnapshotRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MasterSnapshotAcceptedResponse> IngestStockItemsAsync(PushStockItemSnapshotRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MasterSnapshotAcceptedResponse> IngestVoucherTypesAsync(PushVoucherTypeSnapshotRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MasterFreshnessSummaryResponse> GetFreshnessSummaryAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
