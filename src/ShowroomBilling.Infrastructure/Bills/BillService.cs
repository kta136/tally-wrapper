using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Health;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Bills;

public sealed class BillService : IBillService
{
    private readonly BillReadWorkflow _read;
    private readonly BillLifecycleWorkflow _lifecycle;
    private readonly BillPostingWorkflow _posting;
    private readonly BillAuditStore _audit;
    private readonly BillAdminWorkflow _admin;

    public BillService(
        ShowroomBillingDbContext dbContext,
        INumberingService numberingService,
        ITallyPoster tallyPoster,
        ITallyCompanyHealthService? tallyCompanyHealthService = null,
        ILoggerFactory? loggerFactory = null,
        IAuditActorContext? auditActorContext = null)
    {
        var postingLogger = loggerFactory?.CreateLogger<BillPostingWorkflow>()
            ?? NullLogger<BillPostingWorkflow>.Instance;
        _audit = new BillAuditStore(dbContext, auditActorContext);
        _read = new BillReadWorkflow(dbContext);
        _lifecycle = new BillLifecycleWorkflow(dbContext, numberingService, _audit);
        _posting = new BillPostingWorkflow(dbContext, tallyPoster, _audit, postingLogger, tallyCompanyHealthService);
        _admin = new BillAdminWorkflow(dbContext, numberingService, _audit);
    }

    public Task<BillResponse> CreateDraftAsync(
        CreateBillDraftRequest request,
        CancellationToken cancellationToken = default)
        => _lifecycle.CreateDraftAsync(request, cancellationToken);

    public Task<BillResponse> CreateBackdatedDraftAsync(
        CreateBillDraftRequest request,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
        => _lifecycle.CreateBackdatedDraftAsync(request, createdAtUtc, cancellationToken);

    public Task<BillResponse> UpdateDraftAsync(
        Guid billId,
        UpdateBillDraftRequest request,
        CancellationToken cancellationToken = default)
        => _lifecycle.UpdateDraftAsync(billId, request, cancellationToken);

    public Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
        => _read.GetAsync(billId, cancellationToken);

    public Task<BillBatchGetResponse> GetManyAsync(
        BillBatchGetRequest request,
        CancellationToken cancellationToken = default)
        => _read.GetManyAsync(request, cancellationToken);

    public Task<BillListResponse> SearchAsync(
        BillSearchFilter filter,
        CancellationToken cancellationToken = default)
        => _read.SearchAsync(filter, cancellationToken);

    public Task<BillResponse> PushAsync(
        Guid billId,
        PushBillRequest request,
        CancellationToken cancellationToken = default)
        => _posting.PushAsync(billId, request, cancellationToken);

    public Task<BillBatchPushResponse> PushSelectedAsync(
        PushSelectedBillsRequest request,
        CancellationToken cancellationToken = default)
        => _posting.PushSelectedAsync(request, cancellationToken);

    public Task<BillBatchPushResponse> PushPendingAsync(
        PushPendingBillsRequest request,
        CancellationToken cancellationToken = default)
        => _posting.PushPendingAsync(request, cancellationToken);

    public Task<BillResponse> ReviseAsync(
        Guid billId,
        ReviseBillRequest request,
        CancellationToken cancellationToken = default)
        => _lifecycle.ReviseAsync(billId, request, cancellationToken);

    public Task<BillResponse> VoidAsync(
        Guid billId,
        VoidBillRequest request,
        CancellationToken cancellationToken = default)
        => _lifecycle.VoidAsync(billId, request, cancellationToken);

    public Task<BillAuditResponse?> GetAuditAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
        => _audit.GetAuditAsync(billId, cancellationToken);

    public Task<BillPostingStatusResponse?> GetPostingStatusAsync(
        Guid billId,
        CancellationToken cancellationToken = default)
        => _audit.GetPostingStatusAsync(billId, cancellationToken);

    public Task<BillPostingStatusResponse> RetryAsync(
        Guid billId,
        RetryBillPostingRequest request,
        CancellationToken cancellationToken = default)
        => _posting.RetryAsync(billId, request, cancellationToken);

    public Task<BillPostingStatusResponse> RepostAsync(
        Guid billId,
        RepostBillRequest request,
        CancellationToken cancellationToken = default)
        => _posting.RepostAsync(billId, request, cancellationToken);

    public Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(
        Guid billId,
        ChangeBillNumberRequest request,
        CancellationToken cancellationToken = default)
        => _admin.ChangeInvoiceNumberAsync(billId, request, cancellationToken);

    public Task<BillResponse> MarkPostedAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default)
        => _admin.MarkPostedAsync(billId, request, cancellationToken);

    public Task<BillResponse> MarkPendingAsync(
        Guid billId,
        MarkBillStateRequest request,
        CancellationToken cancellationToken = default)
        => _admin.MarkPendingAsync(billId, request, cancellationToken);

    public Task<DeleteBillResponse> DeleteAsync(
        Guid billId,
        DeleteBillRequest request,
        CancellationToken cancellationToken = default)
        => _admin.DeleteAsync(billId, request, cancellationToken);

    public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(
        DeleteSelectedBillsRequest request,
        CancellationToken cancellationToken = default)
        => _admin.DeleteSelectedAsync(request, cancellationToken);
}
