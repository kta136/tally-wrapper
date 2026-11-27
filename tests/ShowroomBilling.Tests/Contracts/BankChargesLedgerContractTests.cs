using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// The bank charges ledger on <c>GET</c> / <c>PUT /api/settings</c>: empty on a new database, saved
/// when a counter sends it, cleared when it sends it empty, and kept when a counter older than the
/// field saves the settings without it.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class BankChargesLedgerContractTests : IClassFixture<TestApiFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly TestApiFactory _factory;

    public BankChargesLedgerContractTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_ledger_is_saved_cleared_and_kept_by_a_counter_that_does_not_know_it()
    {
        var client = CreateAuthenticatedClient();

        var fresh = await GetSettingsAsync(client);
        Assert.Equal(string.Empty, fresh.Settings.Ledgers.BankChargesLedger);
        using (var raw = JsonDocument.Parse(await client.GetStringAsync("/api/settings")))
        {
            var ledgers = raw.RootElement.GetProperty("settings").GetProperty("ledgers");
            Assert.Equal(JsonValueKind.String, ledgers.GetProperty("bankChargesLedger").ValueKind);
        }

        await PutAsync(client, WithBankChargesLedger(fresh.Settings, "Bank Charges Collected"));
        Assert.Equal("Bank Charges Collected", (await GetSettingsAsync(client)).Settings.Ledgers.BankChargesLedger);

        // An older counter's save has no such property at all.
        var oldCounterBody = JsonSerializer.SerializeToNode(
            new UpdateEffectiveSettingsRequest(WithBankChargesLedger(fresh.Settings, null)), Web)!.AsObject();
        oldCounterBody["settings"]!["ledgers"]!.AsObject().Remove("bankChargesLedger");
        var oldSave = await client.PutAsync(
            "/api/settings",
            new StringContent(oldCounterBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, oldSave.StatusCode);
        Assert.Equal("Bank Charges Collected", (await GetSettingsAsync(client)).Settings.Ledgers.BankChargesLedger);

        // An explicit null is the same: the field is not known.
        await PutAsync(client, WithBankChargesLedger(fresh.Settings, null));
        Assert.Equal("Bank Charges Collected", (await GetSettingsAsync(client)).Settings.Ledgers.BankChargesLedger);

        await PutAsync(client, WithBankChargesLedger(fresh.Settings, "  "));
        Assert.Equal(string.Empty, (await GetSettingsAsync(client)).Settings.Ledgers.BankChargesLedger);
    }

    [Fact]
    public async Task A_ledger_name_longer_than_256_characters_is_refused()
    {
        var client = CreateAuthenticatedClient();
        var settings = (await GetSettingsAsync(client)).Settings;

        var response = await client.PutAsJsonAsync(
            "/api/settings",
            new UpdateEffectiveSettingsRequest(WithBankChargesLedger(settings, new string('B', 257))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static EffectiveCloudSettingsDto WithBankChargesLedger(EffectiveCloudSettingsDto settings, string? ledger)
        => settings with { Ledgers = settings.Ledgers with { BankChargesLedger = ledger } };

    private static async Task<EffectiveSettingsResponse> GetSettingsAsync(HttpClient client)
        => (await client.GetFromJsonAsync<EffectiveSettingsResponse>("/api/settings", Web))!;

    private static async Task PutAsync(HttpClient client, EffectiveCloudSettingsDto settings)
    {
        var response = await client.PutAsJsonAsync("/api/settings", new UpdateEffectiveSettingsRequest(settings), Web);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, _factory.GetDeviceToken());
        return client;
    }
}
