using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Health;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Infrastructure.Bills;
using ShowroomBilling.Infrastructure.Numbering;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests.Postgres;

internal static class PostgresBillTestSupport
{
    internal static BillService BuildService(
        ShowroomBillingDbContext db,
        ITallyPoster? poster = null,
        ITallyCompanyHealthService? tallyHealth = null)
    {
        var numbering = new NumberingService(db);
        return new BillService(db, numbering, poster ?? new RecordingTallyPoster(), tallyHealth ?? HealthyTallyHealth.Instance);
    }

    internal static BillPayloadDto SamplePayload(string party, decimal grandTotal) =>
        new(
            PartyName: party,
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: DateOnly.FromDateTime(DateTime.UtcNow.Date),
            Lines:
            [
                new BillLineItemDto("22K Gold Ring", "7113", 10m, "grams", grandTotal / 10m, grandTotal, "22K", null)
            ],
            Totals: new BillTotalsDto(grandTotal, 0m, 0m, 0m, grandTotal),
            Notes: null);

    internal static TallyPostResponse PostedResponse(string? remoteId = "FAKE-VCH-1", string? tallyMasterId = null) =>
        new(TallyPostOutcome.Posted, remoteId, null, null, "voucher-import-v1", null, null, tallyMasterId);
}

internal sealed class RecordingTallyPoster : ITallyPoster
{
    private readonly Lock gate = new();
    private readonly Queue<TallyPostResponse> responses = new();
    private readonly List<TallyPostRequest> requests = [];

    public RecordingTallyPoster(params TallyPostResponse[] responses)
    {
        foreach (var response in responses)
        {
            this.responses.Enqueue(response);
        }
    }

    public int CallCount { get; private set; }

    public IReadOnlyList<TallyPostRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return requests.ToArray();
            }
        }
    }

    public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            CallCount++;
            requests.Add(request);
            return Task.FromResult(responses.Count > 0
                ? responses.Dequeue()
                : PostgresBillTestSupport.PostedResponse());
        }
    }
}

internal sealed class BlockingTallyPoster : ITallyPoster
{
    private readonly TaskCompletionSource<TallyPostRequest> firstRequest =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource<TallyPostResponse> release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int callCount;

    public int CallCount => Volatile.Read(ref callCount);

    public Task<TallyPostRequest> FirstRequest => firstRequest.Task;

    public void ReleasePosted(string? remoteId = "FAKE-VCH-1", string? tallyMasterId = null)
    {
        release.TrySetResult(PostgresBillTestSupport.PostedResponse(remoteId, tallyMasterId));
    }

    public async Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref callCount);
        firstRequest.TrySetResult(request);
        return await release.Task.WaitAsync(cancellationToken);
    }
}

internal sealed class HealthyTallyHealth : ITallyCompanyHealthService
{
    internal static HealthyTallyHealth Instance { get; } = new();

    public Task<TallyCompanyHealthResponse> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TallyCompanyHealthResponse(
            Status: "healthy",
            TallyReachable: true,
            ActiveCompanyName: "Test Company",
            ActiveCompanyOpen: true,
            CompanyCount: 1,
            CheckedAtUtc: DateTimeOffset.UtcNow,
            Message: "Tally OK - active company 'Test Company' is open."));
}
