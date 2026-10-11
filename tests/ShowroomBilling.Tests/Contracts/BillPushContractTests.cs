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
            ["Testing:Bills:PushThrowsTallyPreflight"] = "true"
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());

        var pushResponse = await client.PostAsJsonAsync(
            $"/api/bills/{Guid.NewGuid()}/push",
            new PushBillRequest("contract-test"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, pushResponse.StatusCode);
        using var problem = JsonDocument.Parse(await pushResponse.Content.ReadAsStringAsync());
        Assert.Equal("Tally unavailable", problem.RootElement.GetProperty("title").GetString());
        Assert.Contains("Tally push blocked", problem.RootElement.GetProperty("detail").GetString());
    }
}
