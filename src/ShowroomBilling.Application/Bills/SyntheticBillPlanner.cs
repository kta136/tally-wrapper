using ShowroomBilling.Contracts;
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
///      [MinBillTotal, MaxBillAmount] ≤ ₹1,99,000.
///   2. For each bill, pick a distinct minute-slot inside [StartAtUtc, EndAtUtc], which must lie
///      between the first minute after the last bill already stored (worked out by the server's
///      <c>SyntheticBatchExecutor.EarliestBillTimeAsync</c>) and now, so a batch is never dated
///      before a bill already stored nor in the future. With shop hours set, only a minute the shop
///      is open (<see cref="BatchOpenMinutes"/>), and the bills a gap apart when there is room.
///   3. Split each bill's total into 1..N line shares, each enough for the smallest weight of the
///      dearest item.
///   4. Build each line from a random item (never a diamond or stone one, <see cref="MayBill"/>) ×
///      mapped karat, weighing what its share buys in steps of <see cref="SyntheticBatchPlanLimits.WeightStep"/>,
///      then add steps until the bill comes to its total. Totals via <see cref="BillCalculator"/>.
///
/// The planner does NOT reserve invoice numbers or write to DB; the executor does that per-bill.
/// </summary>
public sealed class SyntheticBillPlanner
{
    public readonly record struct PlannedBill(BillPayloadDto Payload, DateTimeOffset ScheduledAtUtc);

    public readonly record struct SyntheticBatchPlan(
        IReadOnlyList<PlannedBill> Bills,
        decimal TotalAmount);

    /// <summary>An item at a karat, and what a gram of it is billed at.</summary>
    private sealed record LinePricing(ItemMasterEntry Item, KaratMasterEntry Karat, BillCalculator.LineInputs Inputs)
    {
        public decimal Rate { get; } = BillCalculator.ComputeEffectiveRate(Inputs);

        public decimal TotalFor(decimal weight)
            => BillCalculator.ComputeLine(Inputs with { NetWeight = weight }).LineTotalInclusive;
    }

    /// <summary>Words in an item's name that make it a diamond or stone item.</summary>
    private static readonly string[] LeftOutNameWords = ["diamond", "stone"];

    /// <summary>
    /// The ways a unit says grams. Settings offers "gm", but item lists saved by older builds say
    /// "g", and v1.4 took only "gm", so it left every gold item of such a list out.
    /// </summary>
    private static readonly HashSet<string> GramUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "g", "gm", "gms", "gram", "grams",
    };

    /// <param name="earliestUtc">The first minute a bill may be dated, just after the last bill already stored, or null when there is none.</param>
    /// <param name="nowUtc">The time now: no bill is dated later.</param>
    /// <param name="hours">The shop hours the bills are dated in, or null for any time; see <see cref="BuildRandomSchedule"/>.</param>
    public SyntheticBatchPlan BuildPlan(
        SyntheticBatchRequest request,
        IReadOnlyList<ItemMasterEntry> itemEntries,
        IReadOnlyList<KaratMasterEntry> karatEntries,
        Random rng,
        DateTimeOffset? earliestUtc,
        DateTimeOffset nowUtc,
        BatchHoursSettings? hours = null)
    {
        ValidateRequest(request);

        if (itemEntries is null || itemEntries.Count == 0)
            throw new RequestValidationException("At least one item master entry is required.");
        if (karatEntries is null || karatEntries.Count == 0)
            throw new RequestValidationException("At least one karat master entry is required.");

        var billableItems = itemEntries.Where(MayBill).ToList();
        if (billableItems.Count == 0)
            throw new RequestValidationException(
                "No item master can be used: a batch leaves out diamond and stone items (the Diamond category, items not sold by the gram, and names with \"diamond\" or \"stone\"). Add a gold item sold by the gram in Settings → Masters.");

        var mappedKarats = karatEntries
            .Where(k => !string.IsNullOrWhiteSpace(k.TallyItem))
            .ToList();
        if (mappedKarats.Count == 0)
            throw new RequestValidationException("At least one karat master entry must map to a Tally stock item.");

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
                throw new RequestValidationException("Selected karats are not mapped to a Tally stock item.");
        }

        // A rate under ₹1 a gram is no price; it would also let a weight step add nothing.
        var pricings = (
                from item in billableItems
                from karat in mappedKarats
                select new LinePricing(item, karat, BuildLineInputs(request, item, karat, netWeight: 0m)))
            .Where(p => p.Rate >= 1m)
            .ToList();
        if (pricings.Count == 0)
            throw new RequestValidationException("No item has a price at this 24kt rate. Check the rate and the karat purities.");

        var billTotals = PartitionBillTotals(
            totalAmount: request.TotalAmount,
            maxBillAmount: request.MaxBillAmount,
            rng: rng);

        var scheduled = BuildRandomSchedule(
            billCount: billTotals.Count,
            startAtUtc: request.StartAtUtc,
            endAtUtc: request.EndAtUtc,
            earliestUtc: earliestUtc,
            nowUtc: nowUtc,
            rng: rng,
            hours: hours);

        var planned = new List<PlannedBill>(billTotals.Count);
        decimal grandTotalSum = 0m;
        for (var i = 0; i < billTotals.Count; i++)
        {
            var scheduledAt = scheduled[i];
            var bill = BuildPlannedBill(
                billTotal: billTotals[i],
                request: request,
                pricings: pricings,
                voucherDate: IndiaTime.DateOf(scheduledAt),
                scheduledAtUtc: scheduledAt,
                rng: rng);
            planned.Add(bill);
            grandTotalSum += bill.Payload.Totals.GrandTotal;
        }

        return new SyntheticBatchPlan(planned, grandTotalSum);
    }

    /// <summary>
    /// Whether a batch may bill this item. Diamond jewellery and stones are left out (owner,
    /// 20 November 2026): an item of the Diamond category, one not sold by the gram (a stone sold
    /// by the carat), and one whose name says diamond or stone, such as a gold-based "Diamond
    /// Earring" or "Stone Ring". A unit of "g", "gm", "gms", "gram" or "grams", in any case, or
    /// none, is grams.
    /// </summary>
    public static bool MayBill(ItemMasterEntry item)
        => !string.Equals(item.ItemCategory?.Trim(), ItemCategories.Diamond, StringComparison.OrdinalIgnoreCase)
           && GramUnits.Contains((item.Unit ?? string.Empty).Trim().TrimEnd('.'))
           && !LeftOutNameWords.Any(word => (item.Name ?? string.Empty).Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The fewest and the most bills <see cref="PartitionBillTotals"/> can make: the fewest when every
    /// bill is the max and what is left under the minimum is dropped, the most when every bill is the minimum.
    /// </summary>
    public static (int Min, int Max) EstimateBillCountBounds(long totalAmount, long maxBillAmount)
    {
        if (totalAmount < SyntheticBatchPlanLimits.MinBillTotal) return (0, 0);
        if (maxBillAmount <= 0) throw new ArgumentOutOfRangeException(nameof(maxBillAmount));

        var min = 1 + (int)((totalAmount - SyntheticBatchPlanLimits.MinBillTotal) / Math.Max(maxBillAmount, SyntheticBatchPlanLimits.MinBillTotal));
        var max = Math.Max(min, (int)(totalAmount / SyntheticBatchPlanLimits.MinBillTotal));
        return (min, max);
    }

    // ── Primitives (public for testability) ─────────────────────────────────

    public static void ValidateRequest(SyntheticBatchRequest request)
    {
        if (request.TotalAmount < SyntheticBatchPlanLimits.MinBillTotal)
            throw new RequestValidationException($"Total target amount must be at least ₹{SyntheticBatchPlanLimits.MinBillTotal:N0}, the smallest bill a batch makes.");
        if (request.MaxBillAmount < SyntheticBatchPlanLimits.MinBillTotal)
            throw new RequestValidationException($"Max bill amount must be at least ₹{SyntheticBatchPlanLimits.MinBillTotal:N0}.");
        if (request.MaxBillAmount > SyntheticBatchPlanLimits.HardMaxBillAmount)
            throw new RequestValidationException($"Max bill amount cannot exceed ₹{SyntheticBatchPlanLimits.HardMaxBillAmount:N0}.");
        if (request.Rate24Kt <= 0m)
            throw new RequestValidationException("24kt rate must be greater than zero.");
        if (request.MinItemsPerBill <= 0)
            throw new RequestValidationException("Minimum items per bill must be at least 1.");
        if (request.MaxItemsPerBill < request.MinItemsPerBill)
            throw new RequestValidationException("Maximum items per bill must be >= minimum.");
        if (request.MaxItemsPerBill > SyntheticBatchPlanLimits.MaxItemsPerBillCap)
            throw new RequestValidationException($"Maximum items per bill cannot exceed {SyntheticBatchPlanLimits.MaxItemsPerBillCap}.");
        if (request.StartAtUtc >= request.EndAtUtc)
            throw new RequestValidationException("Start time must be before end time.");
    }

    /// <summary>
    /// Random bill totals in [MinBillTotal, maxBillAmount] that add up to <paramref name="totalAmount"/>,
    /// or to as much of it as the bills can hold. What is left under the minimum after the last bill
    /// is no bill of its own: it goes to the bills that have room under the max (owner, 21 November
    /// 2026; V1 dropped it, so a batch came to as much as ₹49,999 under its total). Only what no bill
    /// has room for is not billed, which takes every bill being at the max.
    /// </summary>
    public static IReadOnlyList<long> PartitionBillTotals(long totalAmount, long maxBillAmount, Random rng)
    {
        var min = SyntheticBatchPlanLimits.MinBillTotal;
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

        if (remaining == 0 || totals.Count == 0) return totals;

        // A random part to each bill in a random order, then the rest to whichever still has room,
        // so the leftover does not always land on the same bill.
        var order = Enumerable.Range(0, totals.Count).ToArray();
        rng.Shuffle(order);
        foreach (var wholeRoom in new[] { false, true })
        {
            foreach (var i in order)
            {
                var room = Math.Min(maxBillAmount - totals[i], remaining);
                if (room <= 0) continue;
                var part = wholeRoom ? room : rng.NextInt64(0, room + 1);
                totals[i] += part;
                remaining -= part;
            }
        }

        return totals;
    }

    /// <summary>
    /// <paramref name="billCount"/> distinct minutes in [<paramref name="startAtUtc"/>, <paramref name="endAtUtc"/>],
    /// in order, none before <paramref name="earliestUtc"/>, the first minute after the last bill. The window
    /// may not end after <paramref name="nowUtc"/>, nor start before the minute ahead of <paramref name="earliestUtc"/>:
    /// a counter built before 21 November 2026 offers the last bill's own time as the start, and no bill
    /// goes in that minute.
    /// <para>
    /// With <paramref name="hours"/> (owner, 21 November 2026) a bill is dated only at a minute the
    /// shop is open, as <see cref="BatchOpenMinutes.Count"/> counts them, and a window with fewer of
    /// those than bills is refused. Two bills are at least <see cref="BatchHoursSettings.GapMinutes"/>
    /// apart when the open minutes have room for that (<see cref="BatchOpenMinutes.RoomAtGap"/>);
    /// when they do not, the batch is planned all the same, with the largest gap that fits. The gap
    /// is measured along the open minutes laid end to end, so a night or a closed day between two
    /// bills only puts them further apart. Every placement that keeps the gap is as likely as any other.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> BuildRandomSchedule(
        int billCount,
        DateTimeOffset startAtUtc,
        DateTimeOffset endAtUtc,
        DateTimeOffset? earliestUtc,
        DateTimeOffset nowUtc,
        Random rng,
        BatchHoursSettings? hours = null)
    {
        if (billCount <= 0) return Array.Empty<DateTimeOffset>();

        if (earliestUtc is { } first && first > nowUtc)
            throw new RequestValidationException(
                $"The last bill leaves no time to date a batch in: a batch can be dated from {InIndia(first)}, which is later than now.");
        if (endAtUtc > nowUtc)
            throw new RequestValidationException("End time cannot be later than now.");
        if (startAtUtc >= endAtUtc)
            throw new RequestValidationException("Start time must be before end time.");
        if (earliestUtc is { } allowed && startAtUtc < allowed - TimeSpan.FromMinutes(1))
            throw new RequestValidationException(
                $"Start time cannot be before {InIndia(allowed)}, the first time after the last bill.");

        var earliest = CeilToMinute(startAtUtc);
        if (earliestUtc is { } floor && CeilToMinute(floor) > earliest) earliest = CeilToMinute(floor);
        var latest = FloorToMinute(endAtUtc);
        if (latest < earliest)
            throw new RequestValidationException("Selected time window does not contain any valid minute slots.");

        // The minutes a bill may be dated at, as runs that follow one another: the whole window, or
        // with shop hours what each day has open.
        var runs = BatchOpenMinutes.Runs(earliest, latest, hours).ToList();
        var slotCount = (int)Math.Min(runs.Sum(run => run.Length), int.MaxValue);
        if (slotCount < billCount)
        {
            if (!KeepsHours(hours))
                throw new RequestValidationException(
                    $"Selected window has {slotCount} minute slot(s); {billCount} are required.");
            if (slotCount == 0)
                throw new RequestValidationException(
                    $"The shop is not open at any minute of the selected times (shop hours: {Describe(hours)}). A batch dates bills only while the shop is open; the hours are set in Settings → Admin.");
            throw new RequestValidationException(
                $"The selected times have {slotCount} minute(s) inside the shop hours ({Describe(hours)}); {billCount} bills need one each. Choose a longer time, or change the hours in Settings → Admin.");
        }

        // The gap asked for when the open minutes have room for it, else the largest that fits.
        // Bills g apart on a line of n minutes are bills next to each other on a line of
        // n - (bills - 1) × (g - 1): pick there, then put the g - 1 minutes back after each bill.
        var gap = 1;
        if (billCount > 1)
            gap = Math.Clamp(hours?.GapMinutes ?? 0, 1, (slotCount - 1) / (billCount - 1));
        var spread = gap - 1;

        // Reservoir-style sample without replacement: pick N unique minute offsets.
        var chosen = SampleWithoutReplacement(slotCount - (billCount - 1) * spread, billCount, rng);
        Array.Sort(chosen);
        // In UTC whatever offset the window came with: the database takes no other (a window sent
        // with +05:30 used to fail when its bills were saved).
        var result = new DateTimeOffset[billCount];
        var runIndex = 0;
        var before = 0L;
        for (var i = 0; i < billCount; i++)
        {
            var position = chosen[i] + (long)i * spread;
            while (position >= before + runs[runIndex].Length)
                before += runs[runIndex++].Length;
            result[i] = BatchOpenMinutes.InstantOf(runs[runIndex].FirstMinute + position - before);
        }
        return result;
    }

    /// <summary>Whether these hours leave any minute out: an opening and a closing time, or a closed day.</summary>
    private static bool KeepsHours(BatchHoursSettings? hours)
        => hours is { Opens: not null, Closes: not null } or { ClosedOn: not null };

    /// <summary>The hours in words, for a refusal: "10:00 to 20:00, closed on Tuesday".</summary>
    private static string Describe(BatchHoursSettings? hours)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var times = hours is { Opens: { } opens, Closes: { } closes }
            ? $"{opens.ToString("HH:mm", culture)} to {closes.ToString("HH:mm", culture)}"
            : "any time of day";
        return hours?.ClosedOn is { } day ? $"{times}, closed on {day}" : times;
    }

    /// <summary>
    /// Splits a bill's total into <paramref name="minItems"/>..<paramref name="maxItems"/> shares of at
    /// least <paramref name="minimumEach"/> rupees, as many as fit. Each share gets the minimum, and what
    /// is left is cut at random points among them, so the shares always add up to the total.
    /// </summary>
    public static IReadOnlyList<long> BuildLineTargets(long targetTotal, int minItems, int maxItems, long minimumEach, Random rng)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumEach);

        var feasibleMax = (int)Math.Max(1, Math.Min(maxItems, targetTotal / minimumEach));
        var feasibleMin = Math.Clamp(minItems, 1, feasibleMax);
        var lineCount = rng.Next(feasibleMin, feasibleMax + 1);
        if (lineCount == 1) return [targetTotal];

        var spare = targetTotal - minimumEach * lineCount;
        var cuts = new long[lineCount - 1];
        for (var i = 0; i < cuts.Length; i++)
            cuts[i] = rng.NextInt64(0, spare + 1);
        Array.Sort(cuts);

        var shares = new long[lineCount];
        var previous = 0L;
        for (var i = 0; i < cuts.Length; i++)
        {
            shares[i] = minimumEach + cuts[i] - previous;
            previous = cuts[i];
        }
        shares[^1] = minimumEach + spare - previous;
        return shares;
    }

    private static PlannedBill BuildPlannedBill(
        long billTotal,
        SyntheticBatchRequest request,
        IReadOnlyList<LinePricing> pricings,
        DateOnly voucherDate,
        DateTimeOffset scheduledAtUtc,
        Random rng)
    {
        var lines = BuildLines(billTotal, request, pricings, rng);

        var totals = BillCalculator.BuildTotals(lines.Select(l => l.LineTotal), discount: 0m);
        var paymentMode = PaymentMode.Normalize(request.PaymentMode);

        var payload = new BillPayloadDto(
            // V1 made the party the payment-mode label; since 20 November 2026 it is a customer's name.
            PartyName: SyntheticCustomerNames.Pick(rng),
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
            // The note is the payment, as an operator writes it: it is the second half of the Tally
            // narration. (V1 wrote "Cash" on every bill, so a card bill's voucher said Cash.)
            Notes: paymentMode,
            Payment: paymentMode,
            Rate24Kt: request.Rate24Kt);

        return new PlannedBill(payload, scheduledAtUtc);
    }

    /// <summary>Tries to price one bill this many times before saying it cannot be done.</summary>
    private const int BillAttempts = 20;

    /// <summary>
    /// The lines of one bill: each at least <see cref="SyntheticBatchPlanLimits.MinLineWeight"/>, weighed in
    /// steps of <see cref="SyntheticBatchPlanLimits.WeightStep"/>, coming to a grand total within a step of
    /// <paramref name="billTotal"/>, and never under <see cref="SyntheticBatchPlanLimits.MinBillTotal"/> or
    /// over the max bill amount.
    /// </summary>
    private static List<BillLineItemDto> BuildLines(
        long billTotal,
        SyntheticBatchRequest request,
        IReadOnlyList<LinePricing> pricings,
        Random rng)
    {
        var minWeight = SyntheticBatchPlanLimits.MinLineWeight;
        var affordable = pricings.Where(p => p.Rate * minWeight <= billTotal).ToList();
        if (affordable.Count == 0)
            throw new RequestValidationException(
                $"At this 24kt rate, {minWeight:0.##} g of any item comes to more than ₹{billTotal:N0}. Check the rate.");

        // Only a max bill amount within a couple of weight steps of the minimum can miss; other
        // items, weights and line counts may still land in it.
        for (var attempt = 0; attempt < BillAttempts; attempt++)
        {
            var lines = TryBuildLines(billTotal, request, affordable, rng);
            if (lines is not null) return lines;
        }

        throw new RequestValidationException(
            $"No bill between ₹{SyntheticBatchPlanLimits.MinBillTotal:N0} and ₹{request.MaxBillAmount:N0} can be made in steps of {SyntheticBatchPlanLimits.WeightStep} g at this rate. Raise the max bill amount.");
    }

    /// <summary>One try at <see cref="BuildLines"/>: null when the bill came out over the max bill amount.</summary>
    private static List<BillLineItemDto>? TryBuildLines(
        long billTotal,
        SyntheticBatchRequest request,
        IReadOnlyList<LinePricing> affordable,
        Random rng)
    {
        var minWeight = SyntheticBatchPlanLimits.MinLineWeight;
        var step = SyntheticBatchPlanLimits.WeightStep;

        // Each share buys the smallest weight of the dearest item, so any item can take any share.
        var minimumEach = (long)Math.Ceiling(affordable.Max(p => p.Rate) * minWeight);
        var shares = BuildLineTargets(billTotal, request.MinItemsPerBill, request.MaxItemsPerBill, minimumEach, rng);

        var picked = new LinePricing[shares.Count];
        var weights = new decimal[shares.Count];
        var lineTotals = new decimal[shares.Count];
        for (var i = 0; i < shares.Count; i++)
        {
            picked[i] = affordable[rng.Next(affordable.Count)];
            weights[i] = Math.Max(minWeight, Math.Floor(shares[i] / picked[i].Rate / step) * step);
            lineTotals[i] = picked[i].TotalFor(weights[i]);
        }

        decimal Grand() => BillCalculator.BuildTotals(lineTotals.Sum(), discount: 0m).GrandTotal;

        decimal GrandWith(int line, decimal lineTotal)
            => BillCalculator.BuildTotals(lineTotals.Sum() - lineTotals[line] + lineTotal, discount: 0m).GrandTotal;

        void AddStep(int line)
        {
            weights[line] += step;
            lineTotals[line] = picked[line].TotalFor(weights[line]);
        }

        // Weights rounded down leave each line up to a step short of its share. Add steps, on lines picked
        // at random, while the bill stays within its total.
        while (true)
        {
            var fits = Enumerable.Range(0, shares.Count)
                .Where(i => GrandWith(i, picked[i].TotalFor(weights[i] + step)) <= billTotal)
                .ToList();
            if (fits.Count == 0) break;
            AddStep(fits[rng.Next(fits.Count)]);
        }

        // A total within a step of the minimum can still be under it: the cheapest line takes more.
        var cheapest = Enumerable.Range(0, shares.Count).MinBy(i => picked[i].Rate);
        while (Grand() < SyntheticBatchPlanLimits.MinBillTotal)
            AddStep(cheapest);

        if (Grand() > request.MaxBillAmount)
            return null;

        return Enumerable.Range(0, shares.Count)
            .Select(i => BuildLine(picked[i], weights[i], lineTotals[i]))
            .ToList();
    }

    private static BillLineItemDto BuildLine(LinePricing pricing, decimal weight, decimal lineTotal)
    {
        var (itemEntry, karatEntry) = (pricing.Item, pricing.Karat);
        var stockName = !string.IsNullOrWhiteSpace(karatEntry.TallyItem)
            ? karatEntry.TallyItem.Trim()
            : itemEntry.Name;

        return new BillLineItemDto(
            ItemName: itemEntry.Name,
            HsnCode: JewelleryGst.HsnCode,
            Quantity: weight,
            Unit: string.IsNullOrWhiteSpace(itemEntry.Unit) ? ItemUnits.Gram : itemEntry.Unit,
            Rate: pricing.Rate,
            LineTotal: lineTotal,
            Karat: karatEntry.Label,
            StockName: stockName,
            GrossWeight: weight,
            LessWeight: 0m,
            WastagePercent: itemEntry.WastagePercent,
            LabourPerUnit: itemEntry.DefaultLabourPerGram,
            DiamondRate: null,
            Extra: 0m,
            // What the Invoice tab saves with a line. Without them a bill opened for edit takes the
            // mode from the item master as it is by then, and prices the line again by that.
            ItemCategory: ItemCategories.GoldBased,
            PricingMode: string.IsNullOrWhiteSpace(itemEntry.PricingMode) ? PricingModes.Wastage : itemEntry.PricingMode);
    }

    // A diamond item never gets here (MayBill), so every line is priced by weight.
    private static BillCalculator.LineInputs BuildLineInputs(
        SyntheticBatchRequest request,
        ItemMasterEntry itemEntry,
        KaratMasterEntry karatEntry,
        decimal netWeight)
        => new(
            Rate24Kt: request.Rate24Kt,
            PurityPercent: karatEntry.PurityPercent,
            WastagePercent: itemEntry.WastagePercent,
            LabourPerUnit: itemEntry.DefaultLabourPerGram,
            NetWeight: netWeight,
            ExtraCharges: 0m,
            PricingMode: itemEntry.PricingMode,
            IsDiamond: false,
            DiamondRate: 0m);

    private static DateTimeOffset FloorToMinute(DateTimeOffset v)
        => new(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, v.Offset);

    private static string InIndia(DateTimeOffset instant)
        => instant.ToOffset(IndiaTime.Offset).ToString("dd MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset CeilToMinute(DateTimeOffset v)
    {
        var floored = FloorToMinute(v);
        return floored == v ? floored : floored + TimeSpan.FromMinutes(1);
    }

    /// <summary>Samples k distinct values in [0, population).</summary>
    private static int[] SampleWithoutReplacement(int population, int k, Random rng)
    {
        if (k > population) throw new ArgumentException("k > population", nameof(k));

        // A schedule usually picks a few dozen minutes out of a window of many hundreds.
        // Drawing until k distinct values turn up avoids allocating the whole range;
        // collisions are rare while k is small against the population.
        if ((long)k * 8 <= population)
        {
            var picked = new HashSet<int>(k);
            var sample = new int[k];
            while (picked.Count < k)
            {
                var candidate = rng.Next(population);
                if (picked.Add(candidate))
                {
                    sample[picked.Count - 1] = candidate;
                }
            }
            return sample;
        }

        // Partial Fisher-Yates shuffle, for a window that the bills nearly fill.
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
