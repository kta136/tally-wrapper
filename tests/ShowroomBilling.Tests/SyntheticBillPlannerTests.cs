using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests;

public sealed class SyntheticBillPlannerTests
{
    [Fact]
    public void PartitionBillTotals_RespectsMinAndMaxCaps()
    {
        var rng = new Random(1234);
        var totals = SyntheticBillPlanner.PartitionBillTotals(1_000_000, 199_000, rng);

        Assert.NotEmpty(totals);
        Assert.All(totals, t => Assert.InRange(t, SyntheticBatchPlanLimits.SoftMinBillTotal, 199_000L));
        // Sum is <= target (remainder under SoftMin is dropped, as in V1).
        Assert.True(totals.Sum() <= 1_000_000);
    }

    [Fact]
    public void PartitionBillTotals_BelowSoftMin_YieldsSingleBill()
    {
        var rng = new Random(7);
        var totals = SyntheticBillPlanner.PartitionBillTotals(10_000, 199_000, rng);

        Assert.Single(totals);
        Assert.Equal(10_000, totals[0]);
    }

    [Fact]
    public void BuildRandomSchedule_ProducesSortedDistinctMinuteSlots()
    {
        var rng = new Random(42);
        var start = new DateTimeOffset(2026, 4, 24, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(2);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 30,
            startAtUtc: start,
            endAtUtc: end,
            floorUtc: null,
            rng: rng);

        Assert.Equal(30, slots.Count);
        for (var i = 1; i < slots.Count; i++)
            Assert.True(slots[i] > slots[i - 1], "slots must be strictly increasing");
        Assert.All(slots, t => Assert.InRange(t, start, end));
    }

    [Fact]
    public void BuildRandomSchedule_RespectsFloorUtc()
    {
        var rng = new Random(99);
        var floor = new DateTimeOffset(2026, 4, 24, 0, 15, 0, TimeSpan.Zero);
        var start = floor.AddMinutes(5);
        var end = start.AddHours(1);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 5,
            startAtUtc: start,
            endAtUtc: end,
            floorUtc: floor,
            rng: rng);

        Assert.All(slots, t => Assert.True(t > floor, "slot must be after floor"));
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenStartIsAtOrBeforeFloor()
    {
        var rng = new Random();
        var floor = new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3,
                startAtUtc: floor,
                endAtUtc: floor.AddHours(1),
                floorUtc: floor,
                rng: rng));
    }

    [Fact]
    public void EstimateBillCountBounds_MatchesV1Rules()
    {
        var (minA, maxA) = SyntheticBillPlanner.EstimateBillCountBounds(0, 199_000);
        Assert.Equal((0, 0), (minA, maxA));

        var (minB, maxB) = SyntheticBillPlanner.EstimateBillCountBounds(10_000, 199_000);
        Assert.Equal((1, 1), (minB, maxB));

        var (minC, maxC) = SyntheticBillPlanner.EstimateBillCountBounds(1_000_000, 199_000);
        Assert.True(minC >= 1);
        Assert.True(maxC >= minC);
    }

    [Fact]
    public void BuildPlan_RejectsInvalidInputs()
    {
        var planner = new SyntheticBillPlanner();
        var items = DummyItems();
        var karats = DummyKarats();

        var negative = new SyntheticBatchRequest(-1, 199_000, 7200m, "Cash", 1, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), new[] { "22K" });
        Assert.Throws<ArgumentException>(() =>
            planner.BuildPlan(negative, items, karats, new Random(1), null));

        var maxTooHigh = negative with { TotalAmount = 100_000, MaxBillAmount = 300_000 };
        Assert.Throws<ArgumentException>(() =>
            planner.BuildPlan(maxTooHigh, items, karats, new Random(1), null));

        var emptyKarat = new SyntheticBatchRequest(100_000, 199_000, 7200m, "Cash", 1, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), new[] { "NO-SUCH-KARAT" });
        Assert.Throws<ArgumentException>(() =>
            planner.BuildPlan(emptyKarat, items, karats, new Random(1), null));
    }

    [Fact]
    public void BuildPlan_DeterministicWithSeededRng()
    {
        var planner = new SyntheticBillPlanner();
        var items = DummyItems();
        var karats = DummyKarats();
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(500_000, 199_000, 7200m, "Cash", 1, 2,
            start, start.AddHours(5), new[] { "22K", "18K" });

        var plan1 = planner.BuildPlan(req, items, karats, new Random(2024), null);
        var plan2 = planner.BuildPlan(req, items, karats, new Random(2024), null);

        Assert.Equal(plan1.Bills.Count, plan2.Bills.Count);
        for (var i = 0; i < plan1.Bills.Count; i++)
            Assert.Equal(plan1.Bills[i].ScheduledAtUtc, plan2.Bills[i].ScheduledAtUtc);
    }

    [Fact]
    public void BuildPlan_BillsAreWithinMaxBillCap()
    {
        var planner = new SyntheticBillPlanner();
        var items = DummyItems();
        var karats = DummyKarats();
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(1_500_000, 199_000, 7200m, "Cash", 1, 3,
            start, start.AddHours(24), new[] { "22K" });

        var plan = planner.BuildPlan(req, items, karats, new Random(11), null);

        Assert.NotEmpty(plan.Bills);
        Assert.All(plan.Bills, b =>
        {
            var grand = b.Payload.Totals.GrandTotal;
            // Grand total inclusive of GST can exceed target total by ~3% GST; the cap
            // we enforce is on the partitioned exclusive-of-GST line target, which
            // was sampled ∈ [25k, 199k]. Allow a small GST headroom on the grand.
            Assert.True(grand <= 199_000m * 1.05m, $"bill grand {grand} exceeds cap");
        });
    }

    private static IReadOnlyList<ItemMasterEntry> DummyItems() => new[]
    {
        new ItemMasterEntry(
            Name: "22K Gold Chain",
            Unit: ItemUnits.Gram,
            WastagePercent: 8m,
            ItemCategory: ItemCategories.GoldBased,
            PricingMode: PricingModes.Wastage,
            DefaultLabourPerGram: 0m,
            DefaultStockMappingLabel: "Hallmarked Gold Jewellery 22KT"),
        new ItemMasterEntry(
            Name: "18K Diamond Earring",
            Unit: ItemUnits.Gram,
            WastagePercent: 5m,
            ItemCategory: ItemCategories.GoldBased,
            PricingMode: PricingModes.Both,
            DefaultLabourPerGram: 200m,
            DefaultStockMappingLabel: "IIM Gold Jewellery 18KT"),
    };

    private static IReadOnlyList<KaratMasterEntry> DummyKarats() => new[]
    {
        new KaratMasterEntry("22K", 92.0m, "Hallmarked Gold Jewellery 22KT"),
        new KaratMasterEntry("18K", 75.5m, "IIM Gold Jewellery 18KT"),
    };
}
