using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// What one health poll asks of the server: one request, and a second only when Tally is to be
/// checked, because that one takes as long as Tally takes.
/// </summary>
public sealed class HealthApiClientTests
{
    private const string Health = """
        {"database":{"status":"Healthy","databaseReachable":true,"message":"ok","databaseIdentity":"DEV","pendingMigrationCount":0},
         "masters":{"overallStatus":"fresh","masters":[]}}
        """;

    private const string HealthWithoutLists = """
        {"database":{"status":"Healthy","databaseReachable":true,"message":"ok","databaseIdentity":"DEV","pendingMigrationCount":0},"masters":null}
        """;

    private const string TallyCompany = """
        {"status":"healthy","tallyReachable":true,"activeCompanyName":"Company A","activeCompanyOpen":true,"companyCount":1,"checkedAtUtc":"2026-04-01T10:00:00+00:00","message":"ok"}
        """;

    [Fact]
    public async Task An_ordinary_poll_is_one_request()
    {
        var server = new StubServer { ["/api/health"] = (HttpStatusCode.OK, Health) };
        var client = Client(server);

        var snapshot = await client.GetSnapshotAsync(includeTallyCompany: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["/api/health"], server.Asked);
        Assert.True(snapshot.ApiReachable);
        Assert.True(snapshot.Runtime!.DatabaseReachable);
        Assert.Equal("DEV", snapshot.Runtime.DatabaseIdentity);
        Assert.NotNull(snapshot.Masters);
        Assert.Null(snapshot.TallyCompany);
    }

    [Fact]
    public async Task The_lists_are_not_asked_for_when_they_are_not_due()
    {
        var server = new StubServer { ["/api/health?masters=false"] = (HttpStatusCode.OK, HealthWithoutLists) };
        var client = Client(server);

        var snapshot = await client.GetSnapshotAsync(
            includeTallyCompany: false, includeMasterFreshness: false, TestContext.Current.CancellationToken);

        Assert.Equal(["/api/health?masters=false"], server.Asked);
        Assert.True(snapshot.Runtime!.DatabaseReachable);
        Assert.Null(snapshot.Masters);
    }

    [Fact]
    public async Task The_Tally_check_is_a_second_request_and_its_failure_costs_only_itself()
    {
        var server = new StubServer
        {
            ["/api/health"] = (HttpStatusCode.OK, Health),
            ["/api/health/tally-company"] = (HttpStatusCode.OK, TallyCompany),
        };
        var client = Client(server);

        var snapshot = await client.GetSnapshotAsync(includeTallyCompany: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["/api/health", "/api/health/tally-company"], server.Asked.Order());
        Assert.True(snapshot.TallyCompany!.ActiveCompanyOpen);

        server["/api/health/tally-company"] = (HttpStatusCode.InternalServerError, "");
        snapshot = await client.GetSnapshotAsync(includeTallyCompany: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(snapshot.TallyCompany);
        Assert.True(snapshot.Runtime!.DatabaseReachable);
    }

    [Fact]
    public async Task No_answer_at_all_is_a_server_that_cannot_be_reached()
    {
        var client = Client(new StubServer { Refuses = true });

        var snapshot = await client.GetSnapshotAsync(includeTallyCompany: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(snapshot.ApiReachable);
        Assert.Null(snapshot.Runtime);
    }

    /// <summary>A server older than this Desktop is there, but has no such address: nothing is known of its database.</summary>
    [Fact]
    public async Task An_answer_that_is_not_the_one_expected_leaves_the_server_reachable_and_the_rest_unknown()
    {
        var client = Client(new StubServer());

        var snapshot = await client.GetSnapshotAsync(includeTallyCompany: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(snapshot.ApiReachable);
        Assert.Null(snapshot.Runtime);
        Assert.Null(snapshot.Masters);
    }

    private static HealthApiClient Client(StubServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("http://localhost:5108") });

    /// <summary>Answers the addresses it was given, and 404 for any other.</summary>
    private sealed class StubServer : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, (HttpStatusCode Status, string Body)> _answers = new();

        public ConcurrentQueue<string> Asked { get; } = new();

        public bool Refuses { get; init; }

        public (HttpStatusCode Status, string Body) this[string pathAndQuery]
        {
            set => _answers[pathAndQuery] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Refuses)
            {
                throw new HttpRequestException("No connection could be made.");
            }

            var asked = request.RequestUri!.PathAndQuery;
            Asked.Enqueue(asked);
            return Task.FromResult(_answers.TryGetValue(asked, out var answer)
                ? new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
