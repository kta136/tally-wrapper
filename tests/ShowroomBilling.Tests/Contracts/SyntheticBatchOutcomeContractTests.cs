using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// <c>GET /api/bills/synthetic-batch/{batchId}</c> says how far a batch got, for a Desktop whose
/// request ended without an answer. It is a read, so it needs no token; a batch the server does
/// not know of (it has restarted since) is not found.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class SyntheticBatchOutcomeContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_batch_the_server_does_not_know_of_is_not_found()
    {
        await using var factory = new TestApiFactory(postgres);

        using var response = await factory.CreateClient().GetAsync(
            $"/api/bills/synthetic-batch/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_batch_sent_with_an_id_gives_its_bill_numbers_can_be_asked_about_and_is_not_made_twice()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var cancel = TestContext.Current.CancellationToken;

        // A gold item and a karat mapped to a Tally stock item: what a batch bills.
        var settings = await client.GetFromJsonAsync<EffectiveSettingsResponse>("/api/settings", cancel);
        var masters = new MasterDataSettingsDto(
            JsonSerializer.Serialize(new[] { new ItemMasterEntry("Gold Chain", ItemUnits.Gram, 8m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, "") }),
            JsonSerializer.Serialize(new[] { new KaratMasterEntry("22K", 92m, "Gold 22K") }));
        var saved = await client.PutAsJsonAsync("/api/settings", new UpdateEffectiveSettingsRequest(settings!.Settings with { Masters = masters }), cancel);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync(cancel));

        var setPasscode = await client.PostAsJsonAsync("/api/admin/passcode", new AdminSetPasscodeRequest(null, "111111"), cancel);
        Assert.Equal(HttpStatusCode.NoContent, setPasscode.StatusCode);
        var unlock = await client.PostAsJsonAsync("/api/admin/unlock", new AdminUnlockRequest("111111", "Owner"), cancel);
        var adminToken = (await unlock.Content.ReadFromJsonAsync<AdminUnlockResponse>(cancel))!.Token;

        // Yesterday in India, 10:00 to 14:00: one date, one fiscal year, and wholly in the past.
        var dayStart = IndiaTime.StartOf(IndiaTime.Today().AddDays(-1));
        var batch = new SyntheticBatchRequest(
            TotalAmount: 600_000,
            MaxBillAmount: 199_000,
            Rate24Kt: 7200m,
            PaymentMode: "Cash",
            MinItemsPerBill: 1,
            MaxItemsPerBill: 2,
            StartAtUtc: dayStart.AddHours(10).ToUniversalTime(),
            EndAtUtc: dayStart.AddHours(14).ToUniversalTime(),
            SelectedKaratLabels: ["22K"],
            BatchId: Guid.NewGuid());

        using var first = await SendBatchAsync(client, batch, adminToken, cancel);

        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync(cancel));
        var made = await first.Content.ReadFromJsonAsync<SyntheticBatchResponse>(cancel);
        Assert.True(made!.BillCount >= 3);
        Assert.False(string.IsNullOrWhiteSpace(made.FirstInvoiceNumber));
        Assert.False(string.IsNullOrWhiteSpace(made.LastInvoiceNumber));
        Assert.NotEqual(made.FirstInvoiceNumber, made.LastInvoiceNumber);

        // What the server kept under the id is what it answered.
        var outcome = await client.GetFromJsonAsync<SyntheticBatchOutcomeResponse>($"/api/bills/synthetic-batch/{batch.BatchId}", cancel);
        Assert.Equal(
            new SyntheticBatchOutcomeResponse(true, made.BillCount, made.BillCount, made.TotalAmount, made.FirstInvoiceNumber, made.LastInvoiceNumber),
            outcome);

        // The same batch sent again is refused as already sent, not for its times.
        using var again = await SendBatchAsync(client, batch, adminToken, cancel);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<ProblemDetails>(cancel);
        Assert.StartsWith("This batch has already been sent to the server.", problem!.Detail);
    }

    private static async Task<HttpResponseMessage> SendBatchAsync(HttpClient client, SyntheticBatchRequest batch, string adminToken, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bills/synthetic-batch") { Content = JsonContent.Create(batch) };
        request.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        return await client.SendAsync(request, cancel);
    }

    [Fact]
    public async Task A_batch_is_answered_with_how_far_it_got()
    {
        await using var factory = new TestApiFactory(postgres);
        var client = factory.CreateClient();
        var batch = Guid.NewGuid();
        var outcomes = factory.Services.GetRequiredService<SyntheticBatchOutcomes>();
        outcomes.TryStart(batch, plannedBillCount: 12);
        outcomes.Saved(batch, "A/101", 60_000m);
        outcomes.Saved(batch, "A/102", 70_000m);

        var running = await client.GetFromJsonAsync<SyntheticBatchOutcomeResponse>(
            $"/api/bills/synthetic-batch/{batch}", TestContext.Current.CancellationToken);
        Assert.Equal(new SyntheticBatchOutcomeResponse(false, 12, 2, 130_000m, "A/101", "A/102"), running);

        outcomes.Finish(batch);
        var stopped = await client.GetFromJsonAsync<SyntheticBatchOutcomeResponse>(
            $"/api/bills/synthetic-batch/{batch}", TestContext.Current.CancellationToken);
        Assert.Equal(new SyntheticBatchOutcomeResponse(true, 12, 2, 130_000m, "A/101", "A/102"), stopped);

        // The window keeps its own address beside it.
        using var window = await client.GetAsync("/api/bills/synthetic-batch/window", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, window.StatusCode);
    }
}
