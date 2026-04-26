using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Application.Bills;

/// <summary>
/// Plans a batch of synthetic bills.
///
/// Ported from V1 <c>services/synthetic_bill_generator.py</c>. Pure compute — no DB or HTTP I/O.
///
/// Core algorithm:
///   1. Partition <see cref="SyntheticBatchRequest.TotalAmount"/> into random per-bill totals in
///      [SoftMinBillTotal, MaxBillAmount] ≤ ₹1,99,000.
///   2. For each bill, pick a distinct minute-slot inside [StartAtUtc, EndAtUtc], respecting
///      a floor (the latest existing <c>Bill.CreatedAtUtc</c>) so causality holds.
///   3. Split the per-bill target into 1..N line targets (rejection-sample, fall back to even split).
///   4. Build each line: pick random item × mapped karat, derive qty from target/effRate × random
///      fraction in [QtyFractionMin, QtyFractionMax]. Totals via <see cref="BillCalculator"/>.
///
/// The planner does NOT reserve invoice numbers or write to DB; the executor does that per-bill.
/// </summary>
public sealed class SyntheticBillPlanner
{
    public readonly record struct PlannedBill(BillPayloadDto Payload, DateTimeOffset ScheduledAtUtc);

    public readonly record struct SyntheticBatchPlan(
        IReadOnlyList<PlannedBill> Bills,
        decimal TotalAmount);

    public SyntheticBatchPlan BuildPlan(
        SyntheticBatchRequest request,
        IReadOnlyList<ItemMasterEntry> itemEntries,
        IReadOnlyList<KaratMasterEntry> karatEntries,
        Random rng,
        DateTimeOffset? floorUtc = null)
    {
        ValidateRequest(request);

        if (itemEntries is null || itemEntries.Count == 0)
            throw new ArgumentException("At least one item master entry is required.", nameof(itemEntries));
        if (karatEntries is null || karatEntries.Count == 0)
            throw new ArgumentException("At least one karat master entry is required.", nameof(karatEntries));

        var mappedKarats = karatEntries
            .Where(k => !string.IsNullOrWhiteSpace(k.TallyItem))
            .ToList();
        if (mappedKarats.Count == 0)
            throw new ArgumentException("At least one karat master entry must map to a Tally stock item.", nameof(karatEntries));

        if (request.SelectedKaratLabels is { Count: > 0 })
        {
            var selected = request.SelectedKaratLabels
                .Select(l => l?.Trim() ?? string.Empty)
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            mappedKarats = mappedKarats
                .Where(k => selected.Contains(k.Label.Trim()))
                .ToList();
            if (mappedKarats.Count == 0)
                throw new ArgumentException("Selected karats are not mapped to a Tally stock item.", nameof(request));
        }

        var billTotals = PartitionBillTotals(
            totalAmount: request.TotalAmount,
            maxBillAmount: request.MaxBillAmount,
            rng: rng);

        var scheduled = BuildRandomSchedule(
            billCount: billTotals.Count,
            startAtUtc: request.StartAtUtc,
            endAtUtc: request.EndAtUtc,
            floorUtc: floorUtc,
            rng: rng);

        var planned = new List<PlannedBill>(billTotals.Count);
        decimal grandTotalSum = 0m;
        for (var i = 0; i < billTotals.Count; i++)
        {
            var scheduledAt = scheduled[i];
            var bill = BuildPlannedBill(
                targetTotal: billTotals[i],
                request: request,
                itemEntries: itemEntries,
                karatEntries: mappedKarats,
                voucherDate: DateOnly.FromDateTime(scheduledAt.LocalDateTime),
                scheduledAtUtc: scheduledAt,
                rng: rng);
            planned.Add(bill);
            grandTotalSum += bill.Payload.Totals.GrandTotal;
        }

        return new SyntheticBatchPlan(planned, grandTotalSum);
    }

    /// <summary>Computes the achievable [min, max] bill-count window for a given total+max pair.</summary>
    public static (int Min, int Max) EstimateBillCountBounds(long totalAmount, long maxBillAmount)
    {
        if (totalAmount <= 0) return (0, 0);
        if (maxBillAmount <= 0) throw new ArgumentOutOfRangeException(nameof(maxBillAmount));

        if (totalAmount < SyntheticBatchPlanLimits.SoftMinBillTotal)
            return (1, 1);

        var min = Math.Max(1, (int)Math.Ceiling((double)totalAmount / maxBillAmount));
        var max = Math.Max(min, (int)(totalAmount / SyntheticBatchPlanLimits.SoftMinBillTotal));
        return (min, max);
    }

    // ── Primitives (public for testability) ─────────────────────────────────

    public static void ValidateRequest(SyntheticBatchRequest request)
    {
        if (request.TotalAmount <= 0)
            throw new ArgumentException("Total target amount must be greater than zero.", nameof(request));
        if (request.MaxBillAmount <= 0)
            throw new ArgumentException("Max bill amount must be greater than zero.", nameof(request));
        if (request.MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
            throw new ArgumentException($"Max bill amount cannot exceed ₹{SyntheticBatchPlanLimits.HardMaxBillAmount:N0}.", nameof(request));
        if (request.Rate24Kt <= 0m)
            throw new ArgumentException("24kt rate must be greater than zero.", nameof(request));
        if (request.MinItemsPerBill <= 0)
            throw new ArgumentException("Minimum items per bill must be at least 1.", nameof(request));
        if (request.MaxItemsPerBill < request.MinItemsPerBill)
            throw new ArgumentException("Maximum items per bill must be >= minimum.", nameof(request));
        if (request.MaxItemsPerBill > SyntheticBatchPlanLimits.MaxItemsPerBillCap)
            throw new ArgumentException($"Maximum items per bill cannot exceed {SyntheticBatchPlanLimits.MaxItemsPerBillCap}.", nameof(request));
        if (request.StartAtUtc >= request.EndAtUtc)
            throw new ArgumentException("Start time must be before end time.", nameof(request));
    }

    public static IReadOnlyList<long> PartitionBillTotals(long totalAmount, long maxBillAmount, Random rng)
    {
        var min = SyntheticBatchPlanLimits.SoftMinBillTotal;
        if (totalAmount <= 0) return Array.Empty<long>();
        if (totalAmount < min) return new[] { totalAmount };

        var remaining = totalAmount;
        var totals = new List<long>();
        while (remaining >= min)
        {
            var upper = Math.Min(maxBillAmount, remaining);
            if (upper < min) break;
            var draw = rng.NextInt64(min, upper + 1);
            totals.Add(draw);
            remaining -= draw;
        }

        if (totals.Count == 0)
            totals.Add(Math.Min(maxBillAmount, totalAmount));
        return totals;
    }

    public static IReadOnlyList<DateTimeOffset> BuildRandomSchedule(
        int billCount,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        DateTimeOffset? floorUtc,
        Random rng)
    {
        if (billCount <= 0) return Array.Empty<DateTimeOffset>();
        if (startAtUtc >= endAtUtc)
            throw new ArgumentException("Start time must be before end time.", nameof(startAtUtc));

        if (floorUtc is { } floor && startAtUtc <= floor)
            throw new ArgumentException(
                "Start time must be later than the latest existing bill's timestamp.",
                nameof(startAtUtc));

        var earliest = CeilToMinute(startAtUtc);
        if (floorUtc is { } f2)
        {
            var floorEarliest = FloorToMinute(f2) + TimeSpan.FromMinutes(1);
            if (floorEarliest > earliest) earliest = floorEarliest;
        }
        var latest = FloorToMinute(endAtUtc);
        if (latest < earliest)
            throw new ArgumentException("Selected time window does not contain any valid minute slots.", nameof(endAtUtc));

        var slotCount = (int)((latest - earliest).TotalMinutes) + 1;
        if (slotCount < billCount)
            throw new ArgumentException(
                $"Selected window has {slotCount} minute slot(s); {billCount} are required.",
                nameof(billCount));

        // Reservoir-style sample without replacement: pick N unique minute offsets.
        var chosen = SampleWithoutReplacement(slotCount, billCount, rng);
        Array.Sort(chosen);
        var result = new DateTimeOffset[billCount];
        for (var i = 0; i < billCount; i++)
            result[i] = earliest + TimeSpan.FromMinutes(chosen[i]);
        return result;
    }

    public static IReadOnlyList<long> BuildLineTargets(long targetTotal, int minItems, int maxItems, Random rng)
    {
        var feasibleMax = Math.Max(1, Math.Min(maxItems,
            targetTotal >= SyntheticBatchPlanLimits.MinItemTarget
                ? (int)(targetTotal / SyntheticBatchPlanLimits.MinItemTarget)
                : 1));
        var feasibleMin = Math.Max(1, Math.Min(minItems, feasibleMax));
        var lineCount = rng.Next(feasibleMin, feasibleMax + 1);
        if (lineCount <= 1 || targetTotal <= 0) return new[] { targetTotal };

        var minimumEach = targetTotal >= (long)SyntheticBatchPlanLimits.MinItemTarget * lineCount
            ? SyntheticBatchPlanLimits.MinItemTarget
            : 1;

        for (var attempt = 0; attempt < SyntheticBatchPlanLimits.LineCountRetryAttempts; attempt++)
        {
            if (targetTotal - 1 < lineCount - 1) break;
            var cuts = SampleWithoutReplacement((int)(targetTotal - 1), lineCount - 1, rng)
                .Select(c => (long)(c + 1))
                .OrderBy(c => c)
                .ToArray();
            var slices = new long[lineCount];
            slices[0] = cuts[0];
            for (var i = 1; i < lineCount - 1; i++) slices[i] = cuts[i] - cuts[i - 1];
            slices[lineCount - 1] = targetTotal - cuts[^1];
            if (slices.All(s => s >= minimumEach)) return slices;
        }

        // Fallback: even split.
        var even = targetTotal / lineCount;
        var remainder = (int)(targetTotal - even * lineCount);
        var fallback = Enumerable.Repeat(even, lineCount).ToArray();
        for (var i = 0; i < remainder; i++) fallback[i] += 1;
        return fallback;
    }

    private PlannedBill BuildPlannedBill(
        long targetTotal,
        SyntheticBatchRequest request,
        IReadOnlyList<ItemMasterEntry> itemEntries,
        IReadOnlyList<KaratMasterEntry> karatEntries,
        DateOnly voucherDate,
        DateTimeOffset scheduledAtUtc,
        Random rng)
    {
        var lineTargets = BuildLineTargets(targetTotal, request.MinItemsPerBill, request.MaxItemsPerBill, rng);
        var lines = new List<BillLineItemDto>(lineTargets.Count);
        foreach (var lineTarget in lineTargets)
        {
            var line = BuildLineForTarget(
                targetInclusive: lineTarget,
                request: request,
                itemEntry: itemEntries[rng.Next(itemEntries.Count)],
                karatEntry: karatEntries[rng.Next(karatEntries.Count)]);
            if (line is null)
            {
                // Could not produce a valid line at this rate/karat — fall back to a minimum-qty line
                // so the bill is still persistable. Mirrors V1 _build_fallback_voucher.
                var fallbackItem = itemEntries[rng.Next(itemEntries.Count)];
                var fallbackKarat = karatEntries[rng.Next(karatEntries.Count)];
                line = BuildLine(
                    request: request,
                    itemEntry: fallbackItem,
                    karatEntry: fallbackKarat,
                    qty: SyntheticBatchPlanLimits.MinQty);
            }
            lines.Add(line!);
        }

        var totals = BillCalculator.BuildTotals(lines.Select(l => l.LineTotal), discount: 0m);
        var partyName = PaymentMode.IsCash(request.PaymentMode)
            ? SyntheticBatchPlanLimits.CashPartyName
            : SyntheticBatchPlanLimits.NonCashPartyName;

        var payload = new BillPayloadDto(
            PartyName: partyName,
            PartyGstin: null,
            PartyPhone: null,
            PartyAddress: null,
            BillDate: voucherDate,
            Lines: lines,
            Totals: new BillTotalsDto(
                Subtotal: totals.SubtotalBase,
                DiscountTotal: totals.Discount,
                TaxTotal: totals.Cgst + totals.Sgst,
                RoundOff: totals.RoundOff,
                GrandTotal: totals.GrandTotal),
            Notes: SyntheticBatchPlanLimits.DefaultNarration,
            Payment: PaymentMode.Normalize(request.PaymentMode),
            Rate24Kt: request.Rate24Kt);

        return new PlannedBill(payload, scheduledAtUtc);
    }

    private static BillLineItemDto? BuildLineForTarget(
        long targetInclusive,
        SyntheticBatchRequest request,
        ItemMasterEntry itemEntry,
        KaratMasterEntry karatEntry)
    {
        var rng = new Random(HashCode.Combine(targetInclusive, itemEntry.Name, karatEntry.Label));
        var effRate = BillCalculator.ComputeEffectiveRate(new BillCalculator.LineInputs(
            Rate24Kt: request.Rate24Kt,
            PurityPercent: karatEntry.PurityPercent,
            WastagePercent: itemEntry.WastagePercent,
            LabourPerUnit: itemEntry.DefaultLabourPerGram,
            NetWeight: 0m,
            ExtraCharges: 0m,
            PricingMode: itemEntry.PricingMode,
            IsDiamond: string.Equals(itemEntry.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase),
            DiamondRate: 0m));
        if (effRate <= 0m) return null;

        var fraction = (decimal)(SyntheticBatchPlanLimits.QtyFractionMin
            + (SyntheticBatchPlanLimits.QtyFractionMax - SyntheticBatchPlanLimits.QtyFractionMin) * rng.NextDouble());
        var qty = Math.Max(SyntheticBatchPlanLimits.MinQty, Math.Round(targetInclusive / effRate * fraction, 3));

        var probe = BillCalculator.ComputeLine(new BillCalculator.LineInputs(
            Rate24Kt: request.Rate24Kt,
            PurityPercent: karatEntry.PurityPercent,
            WastagePercent: itemEntry.WastagePercent,
            LabourPerUnit: itemEntry.DefaultLabourPerGram,
            NetWeight: qty,
            ExtraCharges: 0m,
            PricingMode: itemEntry.PricingMode,
            IsDiamond: string.Equals(itemEntry.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase),
            DiamondRate: 0m));
        if (probe.LineTotalInclusive > targetInclusive)
        {
            qty = Math.Max(SyntheticBatchPlanLimits.MinQty, Math.Round(targetInclusive / effRate * 0.5m, 3));
            probe = BillCalculator.ComputeLine(new BillCalculator.LineInputs(
                Rate24Kt: request.Rate24Kt,
                PurityPercent: karatEntry.PurityPercent,
                WastagePercent: itemEntry.WastagePercent,
                LabourPerUnit: itemEntry.DefaultLabourPerGram,
                NetWeight: qty,
                ExtraCharges: 0m,
                PricingMode: itemEntry.PricingMode,
                IsDiamond: string.Equals(itemEntry.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase),
                DiamondRate: 0m));
        }
        if (probe.LineTotalInclusive > targetInclusive)
            qty = SyntheticBatchPlanLimits.MinQty;

        return BuildLine(request, itemEntry, karatEntry, qty);
    }

    private static BillLineItemDto BuildLine(
        SyntheticBatchRequest request,
        ItemMasterEntry itemEntry,
        KaratMasterEntry karatEntry,
        decimal qty)
    {
        var isDiamond = string.Equals(itemEntry.ItemCategory, ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase);
        var inputs = new BillCalculator.LineInputs(
            Rate24Kt: request.Rate24Kt,
            PurityPercent: karatEntry.PurityPercent,
            WastagePercent: itemEntry.WastagePercent,
            LabourPerUnit: itemEntry.DefaultLabourPerGram,
            NetWeight: qty,
            ExtraCharges: 0m,
            PricingMode: itemEntry.PricingMode,
            IsDiamond: isDiamond,
            DiamondRate: 0m);
        var result = BillCalculator.ComputeLine(inputs);

        var stockName = !string.IsNullOrWhiteSpace(karatEntry.TallyItem)
            ? karatEntry.TallyItem.Trim()
            : itemEntry.Name;

        return new BillLineItemDto(
            ItemName: itemEntry.Name,
            HsnCode: "711319",
            Quantity: qty,
            Unit: string.IsNullOrWhiteSpace(itemEntry.Unit) ? ItemUnits.Gram : itemEntry.Unit,
            Rate: result.EffectiveRate,
            LineTotal: result.LineTotalInclusive,
            Karat: karatEntry.Label,
            RawJson: null,
            StockName: stockName,
            GrossWeight: qty,
            LessWeight: 0m,
            WastagePercent: itemEntry.WastagePercent,
            LabourPerUnit: itemEntry.DefaultLabourPerGram,
            DiamondRate: null,
            Extra: 0m);
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset v)
        => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, v.Offset);

    private static DateTimeOffset CeilToMinute(DateTimeOffset v)
    {
        var floored = FloorToMinute(v);
        return floored == v ? floored : floored + TimeSpan.FromMinutes(1);
    }

    /// <summary>Fisher-Yates reservoir sample of k distinct values in [0, population).</summary>
    private static int[] SampleWithoutReplacement(int population, int k, Random rng)
    {
        if (k > population) throw new ArgumentException("k > population", nameof(k));
        var indices = Enumerable.Range(0, population).ToArray();
        for (var i = 0; i < k; i++)
        {
            var j = rng.Next(i, population);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        var result = new int[k];
        Array.Copy(indices, result, k);
        return result;
    }
}
