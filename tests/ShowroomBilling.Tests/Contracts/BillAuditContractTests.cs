using System.Net;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Tests.Contracts;

public sealed class BillAuditContractTests
{
    [Fact]
    public async Task DeviceAuthenticatedMutation_RecordsRequestActor()
    {
        await using var factory = new TestApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var total = 1_000m;
        var payload = new BillPayloadDto(
            "Walk-in",
            null,
            null,
            null,
            DateOnly.FromDateTime(DateTime.UtcNow),
            [new BillLineItemDto("Gold ring", "7113", 10, "grams", 100, total, "22K", null)],
            new BillTotalsDto(total, 0, 0, 0, total),
            null);

        var create = await client.PostAsJsonAsync("/api/bills/drafts", new CreateBillDraftRequest(null, payload));
        var createBody = await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            create.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created, received {(int)create.StatusCode} {create.StatusCode}: {createBody}");
        var bill = await create.Content.ReadFromJsonAsync<BillResponse>();
        Assert.NotNull(bill);

        var audit = await client.GetFromJsonAsync<BillAuditResponse>($"/api/bills/{bill!.Id}/audit");

        Assert.NotNull(audit);
        var created = Assert.Single(audit!.Events, x => x.EventType == "bill.pending.created");
        Assert.Equal("device", created.ActorType);
        Assert.Equal("desktop", created.ActorId);
    }
}
