using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

[Trait("Category", "Postgres")]
public sealed class BillPushContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Push_when_tally_preflight_is_unhealthy_returns_503_and_leaves_bill_pending()
    {
        await using var factory = new TestApiFactory(postgres, new Dictionary<string, string?>
        {
            ["Testing:TallyCompanyHealth:Status"] = "unhealthy"
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var total = 1_000m;
        var created = await client.PostAsJsonAsync("/api/bills/drafts", new CreateBillDraftRequest(null, new BillPayloadDto(
            "Walk-in",
            null,
            null,
            null,
            DateOnly.FromDateTime(DateTime.UtcNow),
            [new BillLineItemDto("Gold ring", "7113", 10, "grams", 100, total, "22K", null)],
            new BillTotalsDto(total, 0, 0, 0, total),
            null)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bill = await created.Content.ReadFromJsonAsync<BillResponse>();

        var pushResponse = await client.PostAsJsonAsync(
            $"/api/bills/{bill!.Id}/push",
            new PushBillRequest("contract-test"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, pushResponse.StatusCode);
        using var problem = JsonDocument.Parse(await pushResponse.Content.ReadAsStringAsync());
        Assert.Equal("Tally unavailable", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Tally push blocked", problem.RootElement.GetProperty("detail").GetString());
        var after = await client.GetFromJsonAsync<BillResponse>($"/api/bills/{bill.Id}");
        Assert.Equal(BillStates.Pending, after!.State);
    }
}
