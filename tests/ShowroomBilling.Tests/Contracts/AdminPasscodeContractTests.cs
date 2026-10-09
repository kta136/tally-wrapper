using System.Net;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Tests.Contracts;

public sealed class AdminPasscodeContractTests
{
    [Fact]
    public async Task Changing_an_existing_passcode_requires_an_admin_session()
    {
        await using var factory = new TestApiFactory();
        var client = factory.CreateClient();

        var initial = await client.PostAsJsonAsync("/api/admin/passcode", new AdminSetPasscodeRequest(null, "111111"));
        Assert.Equal(HttpStatusCode.NoContent, initial.StatusCode);

        var withoutSession = await client.PostAsJsonAsync("/api/admin/passcode", new AdminSetPasscodeRequest("111111", "222222"));
        Assert.Equal(HttpStatusCode.Forbidden, withoutSession.StatusCode);

        var unlock = await client.PostAsJsonAsync("/api/admin/unlock", new AdminUnlockRequest("111111", "test"));
        var session = await unlock.Content.ReadFromJsonAsync<AdminUnlockResponse>();
        using var change = new HttpRequestMessage(HttpMethod.Post, "/api/admin/passcode")
        {
            Content = JsonContent.Create(new AdminSetPasscodeRequest("111111", "222222"))
        };
        change.Headers.Add(AdminTokenConstants.HeaderName, session!.Token);
        var withSession = await client.SendAsync(change);

        Assert.Equal(HttpStatusCode.NoContent, withSession.StatusCode);
    }
}
