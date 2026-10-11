using System.Net.Http.Json;
using ShowroomBilling.Contracts.Runtime;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

[Trait("Category", "Postgres")]
public sealed class RuntimeHealthContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task RuntimeHealth_DefaultProbe_SkipsDatabase()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<RuntimeHealthResponse>("/api/runtime/health");

        Assert.NotNull(response);
        Assert.True(response!.ApiAvailable);
        Assert.True(response.DatabaseHealthSkipped);
        Assert.Contains("skipped", response.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuntimeHealth_ForcedProbe_ChecksDatabase()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<RuntimeHealthResponse>("/api/runtime/health?forceDatabase=true");

        Assert.NotNull(response);
        Assert.True(response!.ApiAvailable);
        Assert.False(response.DatabaseHealthSkipped);
    }
}
