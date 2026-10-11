using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

[Trait("Category", "Postgres")]
public sealed class BillAuditContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task DeviceAuthenticatedMutation_RecordsRequestActor()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());

        var bill = await CreateBillAsync(client);

        var audit = await client.GetFromJsonAsync<BillAuditResponse>($"/api/bills/{bill.Id}/audit");

        Assert.NotNull(audit);
        var created = Assert.Single(audit!.Events, x => x.EventType == "bill.pending.created");
        Assert.Equal("device", created.ActorType);
        Assert.Equal("desktop", created.ActorId);
    }

    [Fact]
    public async Task Editing_a_posted_bill_needs_an_admin_session_and_the_audit_names_the_admin()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var bill = await CreateBillAsync(client);
        var adminToken = await UnlockAdminAsync(client, "Owner");
        using (var mark = WithAdmin(HttpMethod.Post, $"/api/bills/{bill.Id}/mark-posted", new MarkBillStateRequest("Entered in Tally by hand"), adminToken))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(mark)).StatusCode);
        }

        var edit = new UpdateBillDraftRequest(Payload(1_500m));

        var asCounter = await client.PutAsJsonAsync($"/api/bills/drafts/{bill.Id}", edit);

        Assert.Equal(HttpStatusCode.Forbidden, asCounter.StatusCode);
        var problem = await asCounter.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Admin session required", problem!.Title);
        Assert.Contains("Unlock admin", problem.Detail);

        using var asAdminRequest = WithAdmin(HttpMethod.Put, $"/api/bills/drafts/{bill.Id}", edit, adminToken);
        var asAdmin = await client.SendAsync(asAdminRequest);

        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
        var saved = await asAdmin.Content.ReadFromJsonAsync<BillResponse>();
        Assert.Equal(BillStates.Pending, saved!.State);
        var audit = await client.GetFromJsonAsync<BillAuditResponse>($"/api/bills/{bill.Id}/audit");
        var reopened = Assert.Single(audit!.Events, x => x.EventType == BillAuditEvents.EditReopened);
        Assert.Equal("admin", reopened.ActorType);
        Assert.Equal("Owner", reopened.ActorId);
    }

    private static async Task<BillResponse> CreateBillAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/api/bills/drafts", new CreateBillDraftRequest(null, Payload(1_000m)));
        var createBody = await create.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(
            create.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created, received {(int)create.StatusCode} {create.StatusCode}: {createBody}");
        var bill = await create.Content.ReadFromJsonAsync<BillResponse>();
        Assert.NotNull(bill);
        return bill!;
    }

    private static async Task<string> UnlockAdminAsync(HttpClient client, string actorLabel)
    {
        var setPasscode = await client.PostAsJsonAsync("/api/admin/passcode", new AdminSetPasscodeRequest(null, "111111"));
        Assert.Equal(HttpStatusCode.NoContent, setPasscode.StatusCode);
        var unlock = await client.PostAsJsonAsync("/api/admin/unlock", new AdminUnlockRequest("111111", actorLabel));
        var session = await unlock.Content.ReadFromJsonAsync<AdminUnlockResponse>();
        return session!.Token;
    }

    private static HttpRequestMessage WithAdmin(HttpMethod method, string url, object body, string adminToken)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        request.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        return request;
    }

    private static BillPayloadDto Payload(decimal total) => new(
        "Walk-in",
        null,
        null,
        null,
        DateOnly.FromDateTime(DateTime.UtcNow),
        [new BillLineItemDto("Gold ring", "7113", 10, "grams", total / 10, total, "22K", null)],
        new BillTotalsDto(total, 0, 0, 0, total),
        null);
}
