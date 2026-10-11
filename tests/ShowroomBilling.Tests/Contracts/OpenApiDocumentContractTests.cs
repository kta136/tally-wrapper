using System.Net;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// The OpenAPI document is built from the controllers by
/// <c>Microsoft.AspNetCore.OpenApi</c>. It is served over HTTP in Development
/// only, so the first test asks the document service directly: a request or
/// response type the generator cannot describe fails here, not on a
/// developer's first visit to <c>/openapi/v1.json</c>.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class OpenApiDocumentContractTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public OpenApiDocumentContractTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_document_describes_the_controllers()
    {
        var provider = _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");

        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);

        Assert.Contains("/api/bills/drafts", document.Paths.Keys);
        Assert.Contains("/api/bills/{billId}/push", document.Paths.Keys);
        Assert.Contains("/api/runtime/health", document.Paths.Keys);
    }

    [Fact]
    public async Task The_document_is_not_served_outside_Development()
    {
        var client = _factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
