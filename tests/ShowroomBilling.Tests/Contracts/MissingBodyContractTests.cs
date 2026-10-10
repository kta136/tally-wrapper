using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ShowroomBilling.Contracts.Device;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// A request that needs a body and arrives without one is a 400 with a
/// ProblemDetails body, answered by the framework before the action runs.
/// Actions therefore do not check their request for null themselves.
/// </summary>
public sealed class MissingBodyContractTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public MissingBodyContractTests(TestApiFactory factory) => _factory = factory;

    public static TheoryData<string, string> RoutesThatNeedABody()
    {
        var bill = Guid.NewGuid();
        var lease = Guid.NewGuid();
        return new TheoryData<string, string>
        {
            { "POST", "/api/bills/drafts" },
            { "PUT", $"/api/bills/drafts/{bill}" },
            { "POST", "/api/bills/batch" },
            { "POST", "/api/bills/push-selected" },
            { "POST", $"/api/bills/{bill}/repost" },
            { "POST", "/api/clients/heartbeat" },
            { "POST", "/api/leases/drafts/acquire" },
            { "POST", $"/api/leases/drafts/{lease}/renew" },
            { "POST", $"/api/leases/drafts/{lease}/release" },
            { "POST", "/api/print-assets" },
            { "POST", "/api/admin/unlock" },
            { "POST", "/api/admin/logout" },
            { "PUT", "/api/settings" },
        };
    }

    [Theory]
    [MemberData(nameof(RoutesThatNeedABody))]
    public async Task A_request_with_no_body_is_a_400_problem(string method, string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, _factory.GetDeviceToken());
        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.True(problem.RootElement.TryGetProperty("title", out _));
    }
}
