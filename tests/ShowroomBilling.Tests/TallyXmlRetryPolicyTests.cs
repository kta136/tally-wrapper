using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

public sealed class TallyXmlRetryPolicyTests
{
    [Fact]
    public async Task VoucherWritesAreNotRetried_ButReadRequestsRetryTransientServerErrors()
    {
        var handler = new FailOnceHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused;Username=unused;Password=unused"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureLayer(configuration);
        services.RemoveAll<ICloudSettingsService>();
        services.AddSingleton<ICloudSettingsService, FakeCloudSettingsService>();
        services.AddHttpClient<ITallyXmlClient, TallyXmlClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<ITallyXmlClient>();
        var request = new XElement("ENVELOPE");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendWriteAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.CallCount);

        handler.Reset();
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("ENVELOPE", response.Name.LocalName);
        Assert.Equal(2, handler.CallCount);
    }

    private sealed class FailOnceHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public void Reset() => CallCount = 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var response = CallCount == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<ENVELOPE />")
                };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCloudSettingsService : ICloudSettingsService
    {
        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveSettingsResponse(
                "cloud",
                "test",
                new EffectiveCloudSettingsDto(
                    new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Acme Jewellers"),
                    new NumberingSettingsDto("DEV-", "", 4),
                    new PrintSettingsDto("Acme", null, null, null, null, null, null, null, null, null, null, true, false, false, 11, 9),
                    new LedgerMappingsDto("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                    new MasterDataSettingsDto("[]", "[]")),
                [],
                [],
                DateTimeOffset.UtcNow));

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(UpdateEffectiveSettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(string companyName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(UpdatePrintLayoutRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
