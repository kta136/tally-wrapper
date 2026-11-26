using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// Locks the wire shape of <c>POST /api/bills/push-order</c>, which the Desktop asks before a push
/// or retry. A read: it needs no device token.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PushOrderContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Push_order_names_the_earlier_bill_not_in_Tally_yet()
    {
        await using var factory = new TestApiFactory(postgres);
        var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var first = await CreateAsync(writer);
        var second = await CreateAsync(writer);

        // Asked without a device token: it is a read.
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/bills/push-order", new BillPushOrderRequest([second.Id]), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = Assert.Single(doc.RootElement.GetProperty("earlierBills").EnumerateArray().ToList());
        Assert.Equal(["billDate", "billId", "invoiceNumber", "state"], row.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(first.Id, row.GetProperty("billId").GetGuid());
        Assert.Equal(first.InvoiceNumber, row.GetProperty("invoiceNumber").GetString());
        Assert.Equal(BillStates.Pending, row.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Push_order_with_too_many_bills_is_a_400_problem()
    {
        await using var factory = new TestApiFactory(postgres);
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/bills/push-order", new BillPushOrderRequest(ids), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<BillResponse> CreateAsync(HttpClient client)
    {
        var total = 1_000m;
        var created = await client.PostAsJsonAsync("/api/bills/drafts", new CreateBillDraftRequest(new BillPayloadDto(
            "Walk-in",
            null,
            null,
            null,
            DateOnly.FromDateTime(DateTime.UtcNow),
            [new BillLineItemDto("Gold ring", "7113", 10, "grams", 100, total, "22K", null)],
            new BillTotalsDto(total, 0, 0, 0, total),
            null)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<BillResponse>(TestContext.Current.CancellationToken))!;
    }
}
