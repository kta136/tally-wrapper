using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// <c>GET</c> and <c>PUT /api/settings/batch-hours</c>: the shop hours a batch of the Batch Data
/// Scheduler keeps to. Reading them needs no token; saving them is for an admin only, and
/// <c>GET /api/bills/synthetic-batch/window</c> hands them to the scheduler's form.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class BatchHoursContractTests(PostgresFixture postgres)
{
    private const string Address = "/api/settings/batch-hours";

    [Fact]
    public async Task Reading_needs_no_token_and_gives_no_hours_until_some_are_saved()
    {
        await using var factory = new TestApiFactory(postgres);
        var cancel = TestContext.Current.CancellationToken;

        using var response = await factory.CreateClient().GetAsync(Address, cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var read = await response.Content.ReadFromJsonAsync<BatchHoursResponse>(cancel);
        Assert.Equal(new BatchHoursSettings(), read!.Hours);
    }

    [Fact]
    public async Task Saving_is_refused_without_an_admin_session_and_stores_nothing()
    {
        await using var factory = new TestApiFactory(postgres);
        var cancel = TestContext.Current.CancellationToken;
        var request = new UpdateBatchHoursRequest(new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0)));

        // No token at all.
        var bare = factory.CreateClient();
        using var unauthenticated = await bare.PutAsJsonAsync(Address, request, cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        // A counter's device token is not an admin session.
        var counter = factory.CreateClient();
        counter.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        using var deviceOnly = await counter.PutAsJsonAsync(Address, request, cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceOnly.StatusCode);

        // Nor is a token nobody was given.
        using var forged = await SendAsync(bare, request, "not-a-session", cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        var read = await bare.GetFromJsonAsync<BatchHoursResponse>(Address, cancel);
        Assert.Equal(new BatchHoursSettings(), read!.Hours);
    }

    [Fact]
    public async Task An_admin_saves_the_hours_and_they_are_read_back_and_handed_to_the_scheduler()
    {
        await using var factory = new TestApiFactory(postgres);
        var cancel = TestContext.Current.CancellationToken;
        var adminToken = await UnlockAdminAsync(factory, cancel);
        // The admin token alone: this endpoint does not ask for the device token as well.
        var client = factory.CreateClient();

        // Sent with seconds, kept to the minute.
        using var put = await SendAsync(
            client,
            new UpdateBatchHoursRequest(new BatchHoursSettings(new TimeOnly(10, 0, 40), new TimeOnly(20, 30), DayOfWeek.Tuesday, GapMinutes: 5)),
            adminToken,
            cancel);

        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync(cancel));
        var expected = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 30), DayOfWeek.Tuesday, GapMinutes: 5);
        var saved = await put.Content.ReadFromJsonAsync<BatchHoursResponse>(cancel);
        Assert.Equal(expected, saved!.Hours);

        var read = await client.GetFromJsonAsync<BatchHoursResponse>(Address, cancel);
        Assert.Equal(expected, read!.Hours);

        var window = await client.GetFromJsonAsync<SyntheticBatchWindowResponse>("/api/bills/synthetic-batch/window", cancel);
        Assert.Equal(expected, window!.Hours);
        Assert.Null(window.EarliestUtc);

        // Saving hours with nothing in them takes them off again.
        using var cleared = await SendAsync(client, new UpdateBatchHoursRequest(new BatchHoursSettings()), adminToken, cancel);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var none = await client.GetFromJsonAsync<BatchHoursResponse>(Address, cancel);
        Assert.Equal(new BatchHoursSettings(), none!.Hours);
    }

    [Fact]
    public async Task A_bad_value_is_answered_with_a_400_problem_that_says_what_is_wrong()
    {
        await using var factory = new TestApiFactory(postgres);
        var cancel = TestContext.Current.CancellationToken;
        var adminToken = await UnlockAdminAsync(factory, cancel);
        var client = factory.CreateClient();
        (BatchHoursSettings Hours, string Detail)[] refused =
        [
            (new BatchHoursSettings(new TimeOnly(20, 0), new TimeOnly(10, 0)), "Closing time must be later than opening time, on the same day."),
            (new BatchHoursSettings(Opens: new TimeOnly(10, 0)), "Give both an opening time and a closing time, or leave both empty."),
            (new BatchHoursSettings(GapMinutes: BatchHoursLimits.MaxGapMinutes + 1), "The gap between bills must be from 0 to 120 minutes."),
            (new BatchHoursSettings(ClosedOn: (DayOfWeek)9), "The closed day must be a day of the week."),
        ];

        foreach (var (hours, detail) in refused)
        {
            using var response = await SendAsync(client, new UpdateBatchHoursRequest(hours), adminToken, cancel);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancel);
            Assert.Equal(detail, problem!.Detail);
        }

        // Nothing of them was stored.
        var read = await client.GetFromJsonAsync<BatchHoursResponse>(Address, cancel);
        Assert.Equal(new BatchHoursSettings(), read!.Hours);
    }

    private static async Task<string> UnlockAdminAsync(TestApiFactory factory, CancellationToken cancel)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var setPasscode = await client.PostAsJsonAsync("/api/admin/passcode", new AdminSetPasscodeRequest(null, "111111"), cancel);
        Assert.Equal(HttpStatusCode.NoContent, setPasscode.StatusCode);
        var unlock = await client.PostAsJsonAsync("/api/admin/unlock", new AdminUnlockRequest("111111", "Owner"), cancel);
        return (await unlock.Content.ReadFromJsonAsync<AdminUnlockResponse>(cancel))!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, UpdateBatchHoursRequest body, string adminToken, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Address) { Content = JsonContent.Create(body) };
        request.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        return await client.SendAsync(request, cancel);
    }
}
