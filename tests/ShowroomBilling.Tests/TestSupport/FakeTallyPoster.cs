using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Tests.TestSupport;

internal sealed class FakeTallyPoster : ITallyPoster
{
    internal const string DefaultCompany = "Test Company";

    private readonly TallyPostOutcome outcome;
    private readonly string? remoteId;
    private readonly string? tallyMasterId;
    private readonly Queue<TallyPostResponse> responses = new();
    private readonly List<TallyPostRequest> requests = [];

    public FakeTallyPoster(
        TallyPostOutcome outcome = TallyPostOutcome.Posted,
        string? remoteId = "FAKE-VCH-1",
        string? tallyMasterId = null)
    {
        this.outcome = outcome;
        this.remoteId = remoteId;
        this.tallyMasterId = tallyMasterId;
    }

    public FakeTallyPoster(IEnumerable<TallyPostResponse> responses)
        : this()
    {
        foreach (var response in responses)
        {
            this.responses.Enqueue(response);
        }
    }

    public int CallCount { get; private set; }
    public TallyPostRequest? LastRequest { get; private set; }
    public IReadOnlyList<TallyPostRequest> Requests => requests;
    /// <summary>
    /// The company an accepted voucher went to, as the real poster reports
    /// it; the one voucher lookup uses by default.
    /// Null is a push recorded before pushes named their company.
    /// </summary>
    public string? Company { get; init; } = DefaultCompany;

    public Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        requests.Add(request);
        var response = responses.Count > 0
            ? responses.Dequeue()
            : outcome switch
            {
                // Tally answers an Alter with the id of the voucher it altered.
                TallyPostOutcome.Posted => TallyPostResponse.Posted(
                    remoteId,
                    tallyMasterId: request.Operation == TallyPostOperation.Alter ? request.TargetTagValue : tallyMasterId),
                TallyPostOutcome.Unknown => TallyPostResponse.Unknown("FAKE_UNKNOWN", "fake outcome unknown"),
                _ => TallyPostResponse.Failed("FAKE_ERROR", "fake failure")
            };
        return Task.FromResult(response.Outcome == TallyPostOutcome.Posted && Company is not null
            ? response with { Company = Company }
            : response);
    }
}
