using System.Net;
using System.Net.Http.Json;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// <c>GET /api/bills/synthetic-batch/window</c> says when the Batch Data Scheduler may date bills
/// from: the first minute after the last bill, where a bill dated an earlier day than it was saved
/// counts as the end of that day. With it come the shop hours a batch keeps to. It is a read, so
/// it needs no token.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class SyntheticBatchWindowContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task With_no_bill_there_is_no_earliest_time()
    {
        await using var factory = new TestApiFactory(postgres);

        using var response = await factory.CreateClient().GetAsync("/api/bills/synthetic-batch/window", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var window = await response.Content.ReadFromJsonAsync<SyntheticBatchWindowResponse>(TestContext.Current.CancellationToken);
        Assert.Null(window!.EarliestUtc);
        // The shop hours come with it; none have been saved. (BatchHoursContractTests saves some.)
        Assert.Equal(new BatchHoursSettings(), window.Hours);
    }

    [Fact]
    public async Task A_bill_saved_today_and_dated_yesterday_lets_a_batch_start_at_the_start_of_today()
    {
        await using var factory = new TestApiFactory(postgres);
        var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var today = IndiaTime.Today();
        var create = await writer.PostAsJsonAsync(
            "/api/bills/drafts",
            new CreateBillDraftRequest(Payload(today.AddDays(-1))),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var response = await factory.CreateClient().GetAsync("/api/bills/synthetic-batch/window", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var window = await response.Content.ReadFromJsonAsync<SyntheticBatchWindowResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(IndiaTime.StartOf(today), window!.EarliestUtc);
    }

    private static BillPayloadDto Payload(DateOnly billDate) => new(
        "Buyer A",
        null,
        null,
        null,
        billDate,
        [new BillLineItemDto("Gold ring", "711319", 10, "gm", 100, 1_000, "22K", null)],
        new BillTotalsDto(1_000, 0, 0, 0, 1_000),
        null);
}
