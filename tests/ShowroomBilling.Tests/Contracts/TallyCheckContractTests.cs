using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShowroomBilling.Infrastructure.Tally;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// Locks the wire shape of <c>GET /api/tally-check/compare</c>, which Settings →
/// Tally check reads. It is a read: no device token, and nothing is sent to
/// Tally but the one request for the year's vouchers.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class TallyCheckContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Compare_answers_with_the_counts_and_a_row_per_difference()
    {
        await using var factory = new TestApiFactory(postgres);
        var lookup = new ListedVouchers(new TallyVoucherRef("999", new DateOnly(2026, 4, 2), 40m, "Test Company", "BY-HAND/1"));
        var client = WithLookup(factory, lookup);

        var response = await client.GetAsync("/api/tally-check/compare?fiscalYear=2026-27", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = doc.RootElement;
        Assert.Equal(
            ["billsCompared", "billsInOtherCompany", "comparedAtUtc", "differences", "fiscalYear", "matching", "matchingWrittenOtherwise", "pushedWhileComparing", "vouchersInTally"],
            root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("2026-27", root.GetProperty("fiscalYear").GetString());
        Assert.Equal(0, root.GetProperty("billsCompared").GetInt32());
        Assert.Equal(1, root.GetProperty("vouchersInTally").GetInt32());
        Assert.Equal(0, root.GetProperty("matching").GetInt32());
        var row = Assert.Single(root.GetProperty("differences").EnumerateArray().ToList());
        Assert.Equal(
            [
                "billDate", "billId", "billNumber", "billState", "billTotal",
                "dateDiffers", "extraInTally", "missingFromTally", "numberDiffers", "sharedVoucher",
                "tallyDate", "tallyMasterId", "tallyNumber", "tallyTotal", "totalDiffers"
            ],
            row.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(row.GetProperty("extraInTally").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("billId").ValueKind);
        Assert.Equal("BY-HAND/1", row.GetProperty("tallyNumber").GetString());
        Assert.Equal("2026-04-02", row.GetProperty("tallyDate").GetString());
        Assert.Equal(40m, row.GetProperty("tallyTotal").GetDecimal());
        Assert.Equal(["2026-27"], lookup.Asked);
    }

    [Fact]
    public async Task Compare_when_Tally_does_not_answer_is_a_503_problem()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = WithLookup(factory, new ListedVouchers { Failure = new HttpRequestException("No connection could be made") });

        var response = await client.GetAsync("/api/tally-check/compare?fiscalYear=2026-27", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "Tally could not be asked for its sales vouchers of 2026-27: No connection could be made",
            doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Compare_with_a_year_not_written_like_2026_27_is_a_400_problem_and_asks_Tally_nothing()
    {
        await using var factory = new TestApiFactory(postgres);
        var lookup = new ListedVouchers();
        var client = WithLookup(factory, lookup);

        var response = await client.GetAsync("/api/tally-check/compare?fiscalYear=2026", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(lookup.Asked);
    }

    private static HttpClient WithLookup(TestApiFactory factory, ListedVouchers lookup) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITallyVoucherLookup>();
            services.AddSingleton<ITallyVoucherLookup>(lookup);
        })).CreateClient();

    private sealed class ListedVouchers(params TallyVoucherRef[] vouchers) : ITallyVoucherLookup
    {
        public Exception? Failure { get; init; }
        public List<string> Asked { get; } = [];

        public Task<TallyVoucherList> ListSalesVouchersAsync(string fiscalYear, CancellationToken cancellationToken = default)
        {
            Asked.Add(fiscalYear);
            return Failure is null
                ? Task.FromResult(new TallyVoucherList(new TallyVoucherDestination("Test Company", "Sales"), vouchers))
                : Task.FromException<TallyVoucherList>(Failure);
        }

        public Task<IReadOnlyList<TallyVoucherRef>> FindByNumberAsync(string voucherNumber, string fiscalYear, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TallyVoucherRef?> FindByMasterIdAsync(string masterId, string fiscalYear, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TallyVoucherDestination> DestinationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TallyVoucherDestination("Test Company", "Sales"));
    }
}
