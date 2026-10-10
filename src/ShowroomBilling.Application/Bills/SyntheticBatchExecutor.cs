using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Application.Bills;

/// <summary>
/// Orchestrates a synthetic batch run: loads masters, builds a plan via
/// <see cref="SyntheticBillPlanner"/>, and persists each planned bill via
/// <see cref="IBillService.CreateBackdatedDraftAsync"/> one at a time.
///
/// Numbering reservation uses a <c>FOR UPDATE</c> row-lock, so bills must be
/// serialized; per-bill transactions are the correct granularity here.
/// </summary>
public interface ISyntheticBatchExecutor
{
    Task<SyntheticBatchResponse> ExecuteAsync(
        SyntheticBatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SyntheticBatchExecutor(
    IBillService billService,
    ICloudSettingsService settings,
    ILatestBillTimestampProvider latestBillTimestamp,
    ILogger<SyntheticBatchExecutor> logger) : ISyntheticBatchExecutor
{
    public async Task<SyntheticBatchResponse> ExecuteAsync(
        SyntheticBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await settings.GetEffectiveSettingsAsync(cancellationToken);
        var itemEntries = ReadMasters(
            MasterDataJson.ParseItems, effective.Settings.Masters.ItemMasterDataJson, "item masters");
        var karatEntries = ReadMasters(
            MasterDataJson.ParseKarats, effective.Settings.Masters.KaratMappingDataJson, "karat masters");

        if (itemEntries.Count == 0)
            throw new RequestValidationException("No item masters are configured.");
        if (karatEntries.Count == 0)
            throw new RequestValidationException("No karat masters are configured.");

        var floorUtc = await latestBillTimestamp.GetLatestCreatedAtUtcAsync(cancellationToken);

        var rng = new Random();
        var plan = new SyntheticBillPlanner().BuildPlan(request, itemEntries, karatEntries, rng, floorUtc);

        logger.LogInformation(
            "Synthetic batch planned: {BillCount} bill(s), grand ₹{Grand:N0}, window [{Start:o}, {End:o}]",
            plan.Bills.Count, plan.TotalAmount, request.StartAtUtc, request.EndAtUtc);

        var created = new List<SyntheticBatchCreatedBill>(plan.Bills.Count);
        foreach (var planned in plan.Bills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await billService.CreateBackdatedDraftAsync(
                new CreateBillDraftRequest(CounterId: null, Payload: planned.Payload),
                planned.ScheduledAtUtc,
                cancellationToken);

            created.Add(new SyntheticBatchCreatedBill(
                Id: response.Id,
                InvoiceNumber: response.InvoiceNumber,
                GrandTotal: planned.Payload.Totals.GrandTotal,
                ScheduledAtUtc: planned.ScheduledAtUtc));
        }

        return new SyntheticBatchResponse(
            BillCount: created.Count,
            TotalAmount: plan.TotalAmount,
            CreatedBills: created);
    }

    private static IReadOnlyList<T> ReadMasters<T>(Func<string?, IReadOnlyList<T>> parse, string? json, string label)
    {
        try
        {
            return parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse {label} JSON: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Abstraction so the planner can look up "what's the most recent existing bill
/// CreatedAtUtc?" without the executor taking a direct DbContext dependency.
/// Implemented in Infrastructure via a direct EF query.
/// </summary>
public interface ILatestBillTimestampProvider
{
    Task<DateTimeOffset?> GetLatestCreatedAtUtcAsync(CancellationToken cancellationToken = default);
}
