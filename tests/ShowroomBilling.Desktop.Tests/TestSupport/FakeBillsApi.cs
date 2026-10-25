using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>
/// One in-memory <see cref="IBillsApiClient"/> for every Desktop test. Each
/// call is recorded in <see cref="Calls"/> as "verb:billId" (or just "verb"
/// for calls with no single bill); set <see cref="FailWith"/> to make the next
/// write throw, as the real client does on a non-2xx answer.
/// </summary>
internal sealed class FakeBillsApi : IBillsApiClient
{
    public List<string> Calls { get; } = [];
    public Exception? FailWith { get; set; }

    // List and single-bill reads
    public BillListResponse Response { get; set; } = new(0, 0, 50, []);
    public BillSearchFilter? LastFilter { get; private set; }
    public int SearchCount { get; private set; }
    public BillResponse? GetResponse { get; set; }
    public BillAuditResponse? Audit { get; set; }

    /// <summary>What <see cref="GetByOperationAsync"/> finds, by the operation id the bill was created with.</summary>
    public Dictionary<Guid, BillResponse> ByOperation { get; } = [];

    /// <summary>When set, every look-up by operation id fails with it.</summary>
    public Exception? ByOperationFailure { get; set; }

    // Drafts
    public List<CreateBillDraftRequest> Creates { get; } = [];
    public CreateBillDraftRequest? LastCreate => Creates.LastOrDefault();
    public int FailNextCreates { get; set; }
    public List<(Guid BillId, UpdateBillDraftRequest Request, string? AdminToken)> Updates { get; } = [];

    // Admin actions
    public List<Guid> MarkPostedIds { get; } = [];
    public List<string?> MarkPostedReasons { get; } = [];
    public List<Guid> MarkPendingIds { get; } = [];
    public List<string?> MarkPendingReasons { get; } = [];
    public List<string> AdminTokens { get; } = [];
    public bool DeleteDryRunSaysTallyDiverges { get; set; }

    // Synthetic batch
    public SyntheticBatchRequest? LastRequest { get; private set; }
    public string? LastAdminToken => AdminTokens.LastOrDefault();
    public int CallCount { get; private set; }

    public static BillResponse Bill(
        Guid id,
        string state = BillStates.Pending,
        BillPayloadDto? payload = null,
        string? invoiceNumber = "SR/25-26/0001") => new(
        Id: id,
        ShowroomId: Guid.Empty,
        CounterId: null,
        BillType: "sales",
        State: state,
        InvoiceNumber: invoiceNumber,
        FiscalYear: "2025-2026",
        SupersededByBillId: null,
        CurrentRevisionId: payload is null ? null : Guid.NewGuid(),
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        VoidedAtUtc: null,
        CurrentRevision: payload is null
            ? null
            : new BillRevisionResponse(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, null, null, null, payload));

    /// <summary>When set, every search fails with it.</summary>
    public Exception? SearchFailure { get; set; }

    public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default)
    {
        LastFilter = filter;
        SearchCount++;
        return SearchFailure is { } failure
            ? Task.FromException<BillListResponse>(failure)
            : Task.FromResult(Response);
    }

    public Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"get:{billId}");
        return Task.FromResult(GetResponse);
    }

    public Task<BillResponse?> GetByOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"by-operation:{operationId}");
        if (ByOperationFailure is { } failure)
        {
            return Task.FromException<BillResponse?>(failure);
        }

        return Task.FromResult(ByOperation.TryGetValue(operationId, out var bill) ? bill : null);
    }

    public Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new BillBatchGetResponse(GetResponse is null ? [] : [GetResponse]));

    public Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"audit:{billId}");
        return Task.FromResult(Audit);
    }

    public Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default)
    {
        Creates.Add(request);
        if (FailNextCreates > 0)
        {
            FailNextCreates--;
            throw new System.Net.Http.HttpRequestException("connection reset");
        }

        return Task.FromResult(Bill(Guid.NewGuid(), BillStates.Pending, request.Payload));
    }

    public Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        if (adminToken is not null) AdminTokens.Add(adminToken);
        Updates.Add((billId, request, adminToken));
        return Write($"update:{billId}", () => Bill(billId, BillStates.Pending, request.Payload));
    }

    public Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default)
        => Write($"push:{billId}", () => Bill(billId, BillStates.Posted));

    public Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default)
        => Write("push-selected", () => new BillBatchPushResponse(request.BillIds.Count, request.BillIds.Count, 0, false, null, null, []));

    public Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default)
        => Write("push-pending", () => new BillBatchPushResponse(0, 0, 0, false, null, null, []));

    public Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default)
        => Write($"retry:{billId}", () => new BillPostingStatusResponse(billId, BillStates.Posted, null, null, null));

    public Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default)
        => Write($"repost:{billId}", () => new BillPostingStatusResponse(billId, BillStates.Posted, null, null, null));

    public Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default)
        => Write($"void:{billId}", () => Bill(billId, BillStates.Voided));

    public Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default)
        => Write($"revise:{billId}", () => Bill(Guid.NewGuid(), BillStates.Pending, invoiceNumber: "SR/25-26/0002"));

    public Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        AdminTokens.Add(adminToken);
        return Write(
            $"change-number:{billId}:{request.NewInvoiceNumber}",
            () => new ChangeBillNumberResponse(billId, null, request.NewInvoiceNumber, !request.DryRun, false, false, null, null));
    }

    public Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        AdminTokens.Add(adminToken);
        return Write($"mark-posted:{billId}", () =>
        {
            MarkPostedIds.Add(billId);
            MarkPostedReasons.Add(request.Reason);
            return Bill(billId, BillStates.Posted);
        });
    }

    public Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        AdminTokens.Add(adminToken);
        return Write($"mark-pending:{billId}", () =>
        {
            MarkPendingIds.Add(billId);
            MarkPendingReasons.Add(request.Reason);
            return Bill(billId, BillStates.Pending);
        });
    }

    public Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        AdminTokens.Add(adminToken);
        return Write(
            request.DryRun ? $"delete-check:{billId}" : $"delete:{billId}",
            () => new DeleteBillResponse(billId, !request.DryRun, DeleteDryRunSaysTallyDiverges, null, null));
    }

    public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        AdminTokens.Add(adminToken);
        return Write("delete-selected", () => new DeleteSelectedBillsResponse(request.BillIds.Count, request.BillIds.Count, 0, []));
    }

    public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        AdminTokens.Add(adminToken);
        return Task.FromResult(new SyntheticBatchResponse(
            BillCount: 3,
            TotalAmount: 300_000m));
    }

    private Task<T> Write<T>(string call, Func<T> result)
    {
        Calls.Add(call);
        if (FailWith is { } failure)
        {
            FailWith = null;
            return Task.FromException<T>(failure);
        }

        return Task.FromResult(result());
    }
}
