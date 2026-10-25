using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShowroomBilling.Api.Security;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Bills;

namespace ShowroomBilling.Api.Controllers;

[ApiController]
[Route("api/bills")]
public sealed class BillsController(
    BillService billService,
    SyntheticBatchExecutor syntheticBatchExecutor) : ControllerBase
{
    [HttpPost("synthetic-batch")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<SyntheticBatchResponse>> CreateSyntheticBatch(
        [FromBody] SyntheticBatchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await syntheticBatchExecutor.ExecuteAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("drafts")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> CreateDraft(
        [FromBody] CreateBillDraftRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.CreateDraftAsync(request, cancellationToken);
        return Created($"/api/bills/{response.Id}", response);
    }

    [HttpPut("drafts/{billId:guid}")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> UpdateDraft(
        Guid billId,
        [FromBody] UpdateBillDraftRequest request,
        CancellationToken cancellationToken)
    {
        // Any counter may save a pending bill. An edit to a posted bill also
        // needs an admin session; with one, the audit row names the admin.
        var admin = await HttpContext.AuthenticateAsync(AdminAuthenticationHandler.SchemeName);
        if (admin.Succeeded)
        {
            HttpContext.User.AddIdentities(admin.Principal.Identities);
        }

        var response = await billService.UpdateDraftAsync(billId, request, admin.Succeeded, cancellationToken);
        return Ok(response);
    }

    [HttpGet("{billId:guid}")]
    public async Task<ActionResult<BillResponse>> Get(Guid billId, CancellationToken cancellationToken)
    {
        var response = await billService.GetAsync(billId, cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }

    /// <summary>The bill whose create carried this <c>ClientOperationId</c>, at its current revision.</summary>
    [HttpGet("by-operation/{operationId:guid}")]
    public async Task<ActionResult<BillResponse>> GetByOperation(Guid operationId, CancellationToken cancellationToken)
    {
        var response = await billService.GetByOperationIdAsync(operationId, cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPost("batch")]
    public async Task<ActionResult<BillBatchGetResponse>> GetMany(
        [FromBody] BillBatchGetRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.GetManyAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<BillListResponse>> List(
        [FromQuery] string? state,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        [FromQuery] string? sort,
        [FromQuery] bool? includeTotal,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var response = await billService.SearchAsync(
            new BillSearchFilter(state, fromDate, toDate, skip, take, sort, includeTotal, search), cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/push")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> Push(
        Guid billId,
        [FromBody] PushBillRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await billService.PushAsync(
            billId,
            request ?? new PushBillRequest(null),
            cancellationToken);
        return Ok(response);
    }

    [HttpPost("push-selected")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillBatchPushResponse>> PushSelected(
        [FromBody] PushSelectedBillsRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.PushSelectedAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("push-pending")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillBatchPushResponse>> PushPending(
        [FromBody] PushPendingBillsRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await billService.PushPendingAsync(
            request ?? new PushPendingBillsRequest(null, null),
            cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/revise")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> Revise(
        Guid billId,
        [FromBody] ReviseBillRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await billService.ReviseAsync(billId, request ?? new ReviseBillRequest(null), cancellationToken);
        return Created($"/api/bills/{response.Id}", response);
    }

    [HttpGet("{billId:guid}/audit")]
    public async Task<ActionResult<BillAuditResponse>> GetAudit(
        Guid billId,
        CancellationToken cancellationToken)
    {
        var response = await billService.GetAuditAsync(billId, cancellationToken);
        return response is null ? NotFound() : Ok(response);
    }

    [HttpPost("{billId:guid}/retry")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillPostingStatusResponse>> Retry(
        Guid billId,
        [FromBody] RetryBillPostingRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await billService.RetryAsync(
            billId, request ?? new RetryBillPostingRequest(null), cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/repost")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillPostingStatusResponse>> Repost(
        Guid billId,
        [FromBody] RepostBillRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.RepostAsync(billId, request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/void")]
    [Authorize(Policy = DevicePolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> Void(
        Guid billId,
        [FromBody] VoidBillRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await billService.VoidAsync(billId, request ?? new VoidBillRequest(null), cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/change-number")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<ChangeBillNumberResponse>> ChangeNumber(
        Guid billId,
        [FromBody] ChangeBillNumberRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.ChangeInvoiceNumberAsync(billId, request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/mark-posted")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> MarkPosted(
        Guid billId,
        [FromBody] MarkBillStateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.MarkPostedAsync(billId, request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("{billId:guid}/mark-pending")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<BillResponse>> MarkPending(
        Guid billId,
        [FromBody] MarkBillStateRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.MarkPendingAsync(billId, request, cancellationToken);
        return Ok(response);
    }

    [HttpDelete("{billId:guid}")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<DeleteBillResponse>> Delete(
        Guid billId,
        [FromBody] DeleteBillRequest? request,
        CancellationToken cancellationToken)
    {
        var response = await billService.DeleteAsync(billId, request ?? new DeleteBillRequest(null, false), cancellationToken);
        return Ok(response);
    }

    [HttpPost("delete-selected")]
    [Authorize(Policy = AdminPolicy.PolicyName)]
    public async Task<ActionResult<DeleteSelectedBillsResponse>> DeleteSelected(
        [FromBody] DeleteSelectedBillsRequest request,
        CancellationToken cancellationToken)
    {
        var response = await billService.DeleteSelectedAsync(request, cancellationToken);
        return Ok(response);
    }
}
