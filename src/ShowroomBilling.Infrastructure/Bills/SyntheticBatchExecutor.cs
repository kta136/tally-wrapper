using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Bills;

/// <summary>
/// Runs a synthetic batch: loads the masters, builds a plan with
/// <see cref="SyntheticBillPlanner"/>, and saves each planned bill through
/// <see cref="BillService.CreateBackdatedDraftAsync"/>, one at a time.
///
/// A number is reserved under a <c>FOR UPDATE</c> row lock, so the bills are
/// saved one after another, each in its own transaction.
/// </summary>
public sealed class SyntheticBatchExecutor(
    BillService billService,
    ICloudSettingsService settings,
    ShowroomBillingDbContext dbContext,
    ILogger<SyntheticBatchExecutor> logger)
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

        // The planner keeps the batch after the newest bill already stored.
        var floorUtc = await dbContext.Bills
            .AsNoTracking()
            .MaxAsync(b => (DateTimeOffset?)b.CreatedAtUtc, cancellationToken);

        var rng = new Random();
        var plan = new SyntheticBillPlanner().BuildPlan(request, itemEntries, karatEntries, rng, floorUtc);

        logger.LogInformation(
            "Synthetic batch planned: {BillCount} bill(s), grand ₹{Grand:N0}, window [{Start:o}, {End:o}]",
            plan.Bills.Count, plan.TotalAmount, request.StartAtUtc, request.EndAtUtc);

        foreach (var planned in plan.Bills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await billService.CreateBackdatedDraftAsync(
                new CreateBillDraftRequest(CounterId: null, Payload: planned.Payload),
                planned.ScheduledAtUtc,
                cancellationToken);
        }

        return new SyntheticBatchResponse(
            BillCount: plan.Bills.Count,
            TotalAmount: plan.TotalAmount);
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
