using System.Net;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Tests.Postgres;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// <c>GET /api/bills/by-operation/{operationId}</c> finds the bill a create
/// with that <c>ClientOperationId</c> made, at its current revision. It is a
/// read, so it needs no device token.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class BillByOperationContractTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_bill_is_found_by_the_operation_that_created_it_at_its_current_revision()
    {
        await using var factory = new TestApiFactory(postgres);
        var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var operationId = Guid.NewGuid();
        var create = await writer.PostAsJsonAsync(
            "/api/bills/drafts",
            new CreateBillDraftRequest(null, Payload(1_000m), operationId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<BillResponse>(TestContext.Current.CancellationToken);
        var update = await writer.PutAsJsonAsync(
            $"/api/bills/drafts/{created!.Id}",
            new UpdateBillDraftRequest(Payload(2_000m), created.CurrentRevisionId),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        // A read: no device token.
        var reader = factory.CreateClient();
        using var response = await reader.GetAsync($"/api/bills/by-operation/{operationId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var found = await response.Content.ReadFromJsonAsync<BillResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(created.Id, found!.Id);
        Assert.Equal(created.InvoiceNumber, found.InvoiceNumber);
        Assert.Equal(2, found.CurrentRevision!.RevisionNo);
        Assert.Equal(2_000m, found.CurrentRevision.Payload.Totals.GrandTotal);
    }

    [Fact]
    public async Task An_operation_no_bill_was_created_by_is_not_found()
    {
        await using var factory = new TestApiFactory(postgres);
        var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        // A bill saved without an operation id must not be found by an empty or a random one.
        var create = await writer.PostAsJsonAsync(
            "/api/bills/drafts",
            new CreateBillDraftRequest(null, Payload(1_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var client = factory.CreateClient();
        using var random = await client.GetAsync($"/api/bills/by-operation/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        using var empty = await client.GetAsync($"/api/bills/by-operation/{Guid.Empty}", TestContext.Current.CancellationToken);
        using var notAGuid = await client.GetAsync("/api/bills/by-operation/not-a-guid", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, random.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notAGuid.StatusCode);
    }

    private static BillPayloadDto Payload(decimal total) => new(
        "Buyer A",
        null,
        null,
        null,
        DateOnly.FromDateTime(DateTime.UtcNow),
        [new BillLineItemDto("Gold ring", "711319", 10, "gm", total / 10, total, "22K", null)],
        new BillTotalsDto(total, 0, 0, 0, total),
        null);
}
