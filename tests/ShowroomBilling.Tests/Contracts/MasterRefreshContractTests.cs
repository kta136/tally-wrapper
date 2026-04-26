using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// Locks the wire shape of <c>POST /api/masters/refresh</c>. These assertions
/// would have failed when the Desktop's <c>MastersApiClient</c> drifted to
/// expect <c>MasterRefreshAcceptedResponse</c> while the controller returned
/// <c>IReadOnlyList&lt;TallyMasterRefreshResult&gt;</c> — the slice 1 bug.
/// Treat this file as the source of truth for what shape callers see.
/// </summary>
public sealed class MasterRefreshContractTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public MasterRefreshContractTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Refresh_with_no_master_type_returns_array_of_four_result_rows()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/masters/refresh",
            new MasterRefreshRequest(MasterType: null, RequestedByActor: "contract-test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(4, doc.RootElement.GetArrayLength());

        // Every element matches TallyMasterRefreshResult (camelCase JSON).
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, element.ValueKind);
            Assert.Equal(JsonValueKind.String, element.GetProperty("masterType").ValueKind);
            Assert.True(element.TryGetProperty("succeeded", out var succeeded));
            Assert.True(succeeded.ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.Equal(JsonValueKind.Number, element.GetProperty("itemCount").ValueKind);
            Assert.True(element.TryGetProperty("batchId", out _));
            Assert.True(element.TryGetProperty("errorMessage", out _));
        }
    }

    [Fact]
    public async Task Refresh_with_specific_master_type_returns_single_row()
    {
        var client = CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/masters/refresh",
            new MasterRefreshRequest(MasterType: "companies", RequestedByActor: "contract-test"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<IReadOnlyList<TallyMasterRefreshResult>>();
        Assert.NotNull(rows);
        Assert.Single(rows!);
        Assert.Equal("companies", rows![0].MasterType);
        Assert.True(rows[0].Succeeded);
    }

    [Fact]
    public async Task Refresh_without_device_token_returns_401()
    {
        var client = _factory.CreateClient();
        // Note: no X-Device-Token header.

        var response = await client.PostAsJsonAsync(
            "/api/masters/refresh",
            new MasterRefreshRequest(MasterType: null, RequestedByActor: "contract-test"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, _factory.GetDeviceToken());
        return client;
    }
}
