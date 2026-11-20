using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests;

public sealed class SyntheticBillPlannerTests
{
    /// <summary>The time now, for a plan: after every window these tests use.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PartitionBillTotals_RespectsMinAndMaxCaps()
    {
        var rng = new Random(1234);
        var totals = SyntheticBillPlanner.PartitionBillTotals(1_000_000, 199_000, rng);

        Assert.NotEmpty(totals);
        Assert.All(totals, t => Assert.InRange(t, SyntheticBatchPlanLimits.MinBillTotal, 199_000L));
        // Sum is <= target (remainder under the minimum is dropped, as in V1).
        Assert.True(totals.Sum() <= 1_000_000);
    }

    [Fact]
    public void PartitionBillTotals_DropsARemainderUnderTheMinimum_SoFewerBillsThanTotalOverMax()
    {
        // 400,000 at most 199,000 a bill looks like three bills at least. With this seed the
        // first two leave 17,891, which is under the smallest bill there may be, so it is
        // dropped (as in V1) and two bills are the whole batch. No bill is over the cap.
        var totals = SyntheticBillPlanner.PartitionBillTotals(400_000, 199_000, new Random(24));

        Assert.Equal([192_536L, 189_573L], totals);
    }

    [Fact]
    public void PartitionBillTotals_OverManySeeds_NeverExceedsTheCapAndDropsLessThanTheMinimum()
    {
        var fewest = int.MaxValue;
        for (var seed = 0; seed < 20_000; seed++)
        {
            var totals = SyntheticBillPlanner.PartitionBillTotals(400_000, 199_000, new Random(seed));

            Assert.All(totals, t => Assert.InRange(t, SyntheticBatchPlanLimits.MinBillTotal, 199_000L));
            Assert.InRange(400_000 - totals.Sum(), 0, SyntheticBatchPlanLimits.MinBillTotal - 1);
            fewest = Math.Min(fewest, totals.Count);
        }

        // More than 350,000 in bills of at most 199,000 each: never one bill, sometimes only two.
        Assert.Equal(2, fewest);
    }

    [Fact]
    public void PartitionBillTotals_BelowTheMinimum_YieldsNoBill()
    {
        var totals = SyntheticBillPlanner.PartitionBillTotals(49_999, 199_000, new Random(7));

        Assert.Empty(totals);
    }

    [Fact]
    public void BuildLineTargets_GivesEachShareItsMinimumAndAddsUpToTheTotal()
    {
        for (var seed = 0; seed < 2_000; seed++)
        {
            var shares = SyntheticBillPlanner.BuildLineTargets(
                targetTotal: 123_456, minItems: 2, maxItems: 25, minimumEach: 13_000, new Random(seed));

            // 123,456 holds nine shares of 13,000, so never more than nine.
            Assert.InRange(shares.Count, 2, 9);
            Assert.All(shares, share => Assert.True(share >= 13_000, $"seed {seed}: a share of {share}"));
            Assert.Equal(123_456, shares.Sum());
        }
    }

    [Fact]
    public void BuildLineTargets_MakesOneLineWhenTwoMinimumsDoNotFit()
    {
        var shares = SyntheticBillPlanner.BuildLineTargets(
            targetTotal: 50_000, minItems: 3, maxItems: 5, minimumEach: 26_000, new Random(1));

        Assert.Equal([50_000L], shares);
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
            nowUtc: Now,
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
            nowUtc: Now,
            rng: rng);

        Assert.All(slots, t => Assert.True(t > floor, "slot must be after floor"));
    }

    [Fact]
    public void BuildRandomSchedule_MayStartInTheLastBillsMinute_AndPutsNoBillInIt()
    {
        // The dialog shows the last bill's time to the minute, so a window typed from it starts a
        // few seconds before the bill; that is allowed, and every bill goes in a later minute.
        var floor = new DateTimeOffset(2026, 4, 24, 12, 0, 37, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 3, startAtUtc: start, endAtUtc: start.AddMinutes(3), floorUtc: floor, nowUtc: Now, rng: new Random(1));

        Assert.Equal([start.AddMinutes(1), start.AddMinutes(2), start.AddMinutes(3)], slots);
    }

    [Fact]
    public void BuildRandomSchedule_GivesUtcTimes_WhateverOffsetTheWindowCameWith()
    {
        var india = TimeSpan.FromMinutes(330);
        var start = new DateTimeOffset(2026, 4, 24, 10, 0, 0, india);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 5, startAtUtc: start, endAtUtc: start.AddHours(1), floorUtc: null, nowUtc: Now, rng: new Random(3));

        Assert.All(slots, slot =>
        {
            Assert.Equal(TimeSpan.Zero, slot.Offset);
            Assert.InRange(slot, start, start.AddHours(1));
        });
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenStartIsBeforeTheLastBillsMinute()
    {
        var floor = new DateTimeOffset(2026, 4, 24, 12, 0, 37, TimeSpan.Zero);

        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3,
                startAtUtc: floor.AddMinutes(-1),
                endAtUtc: floor.AddHours(1),
                floorUtc: floor,
                nowUtc: Now,
                rng: new Random(1)));
        // Said in India's time: 12:00 UTC is 17:30 there.
        Assert.Equal("Start time cannot be before the last bill, 24 Apr 2026, 17:30.", refused.Message);
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenTheWindowEndsAfterNow()
    {
        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3, startAtUtc: Now.AddHours(-1), endAtUtc: Now.AddMinutes(1), floorUtc: null, nowUtc: Now, rng: new Random(1)));

        Assert.Equal("End time cannot be later than now.", refused.Message);
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenTheLastBillIsLaterThanNow()
    {
        // A batch made before 20 November 2026 could date bills later in the day than it was run.
        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3, startAtUtc: Now.AddHours(-1), endAtUtc: Now, floorUtc: Now.AddHours(2), nowUtc: Now, rng: new Random(1)));

        Assert.StartsWith("The last bill is dated", refused.Message);
        Assert.EndsWith("later than now, so there is no time left to date a batch in.", refused.Message);
    }

    [Fact]
    public void EstimateBillCountBounds_CountsBillsOfTheMinimumToTheCap()
    {
        Assert.Equal((0, 0), SyntheticBillPlanner.EstimateBillCountBounds(0, 199_000));
        Assert.Equal((0, 0), SyntheticBillPlanner.EstimateBillCountBounds(49_999, 199_000));
        Assert.Equal((1, 1), SyntheticBillPlanner.EstimateBillCountBounds(50_000, 199_000));
        // Five bills of 1,99,000 leave 5,000, which is dropped: five, not six.
        Assert.Equal((5, 20), SyntheticBillPlanner.EstimateBillCountBounds(1_000_000, 199_000));
        Assert.Equal((2, 8), SyntheticBillPlanner.EstimateBillCountBounds(400_000, 199_000));
        Assert.Equal((1, 1), SyntheticBillPlanner.EstimateBillCountBounds(50_001, 50_000));
    }

    [Fact]
    public void EstimateBillCountBounds_HoldsWhatThePartitionMakes()
    {
        foreach (var (total, max) in new[] { (1_000_000L, 199_000L), (400_000L, 199_000L), (123_456L, 60_000L), (50_001L, 50_000L) })
        {
            var (fewest, most) = SyntheticBillPlanner.EstimateBillCountBounds(total, max);
            for (var seed = 0; seed < 2_000; seed++)
                Assert.InRange(SyntheticBillPlanner.PartitionBillTotals(total, max, new Random(seed)).Count, fewest, most);
        }
    }

    [Fact]
    public void BuildPlan_RejectsInvalidInputs()
    {
        var planner = new SyntheticBillPlanner();
        var items = DummyItems();
        var karats = DummyKarats();

        var negative = new SyntheticBatchRequest(-1, 199_000, 7200m, "Cash", 1, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), new[] { "22K" });
        Assert.Throws<RequestValidationException>(() =>
            planner.BuildPlan(negative, items, karats, new Random(1), null, Now));

        var maxTooHigh = negative with { TotalAmount = 100_000, MaxBillAmount = 300_000 };
        Assert.Throws<RequestValidationException>(() =>
            planner.BuildPlan(maxTooHigh, items, karats, new Random(1), null, Now));

        // No bill may be under 50,000, so neither the total nor the largest bill may be. (The
        // server formats the amount in its own culture.)
        var minimum = SyntheticBatchPlanLimits.MinBillTotal.ToString("N0");
        var totalTooLow = negative with { TotalAmount = 49_999 };
        Assert.Contains(minimum, Assert.Throws<RequestValidationException>(() =>
            planner.BuildPlan(totalTooLow, items, karats, new Random(1), null, Now)).Message);

        var maxTooLow = negative with { TotalAmount = 100_000, MaxBillAmount = 49_999 };
        Assert.Contains(minimum, Assert.Throws<RequestValidationException>(() =>
            planner.BuildPlan(maxTooLow, items, karats, new Random(1), null, Now)).Message);

        var emptyKarat = new SyntheticBatchRequest(100_000, 199_000, 7200m, "Cash", 1, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), new[] { "NO-SUCH-KARAT" });
        Assert.Throws<RequestValidationException>(() =>
            planner.BuildPlan(emptyKarat, items, karats, new Random(1), null, Now));
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

        var plan1 = planner.BuildPlan(req, items, karats, new Random(2024), null, Now);
        var plan2 = planner.BuildPlan(req, items, karats, new Random(2024), null, Now);

        Assert.Equal(plan1.Bills.Count, plan2.Bills.Count);
        for (var i = 0; i < plan1.Bills.Count; i++)
            Assert.Equal(plan1.Bills[i].ScheduledAtUtc, plan2.Bills[i].ScheduledAtUtc);
    }

    [Theory]
    [InlineData("Cash", PaymentMode.Cash)]
    [InlineData("cash", PaymentMode.Cash)]
    [InlineData("Credit and debit", PaymentMode.CreditDebit)]
    [InlineData("UPI", PaymentMode.CreditDebit)]
    public void BuildPlan_SpellsThePaymentModeOneWay_AndNamesACustomer(string requested, string expected)
    {
        var planner = new SyntheticBillPlanner();
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(300_000, 199_000, 7200m, requested, 1, 2,
            start, start.AddHours(5), new[] { "22K" });

        var plan = planner.BuildPlan(req, DummyItems(), DummyKarats(), new Random(7), null, Now);

        Assert.NotEmpty(plan.Bills);
        Assert.All(plan.Bills, bill =>
        {
            Assert.Equal(expected, bill.Payload.Payment);
            Assert.Equal(SyntheticBatchPlanLimits.DefaultNarration, bill.Payload.Notes);
            // A customer's first name and surname, not the payment mode as V1 had it.
            Assert.Matches(@"^[A-Z][a-z]+ [A-Z][a-z]+$", bill.Payload.PartyName);
        });
    }

    [Fact]
    public void SyntheticCustomerNames_AreAFirstNameAndASurname_DrawnWidely()
    {
        var rng = new Random(11);
        var names = Enumerable.Range(0, 2_000).Select(_ => SyntheticCustomerNames.Pick(rng)).ToList();

        Assert.All(names, name => Assert.Matches(@"^[A-Z][a-z]+ [A-Z][a-z]+$", name));
        // Eleven regions of twenty first names and ten surnames each, about 2,200 names in all.
        Assert.True(names.Distinct().Count() > 1_000, $"only {names.Distinct().Count()} different names");
        Assert.True(names.Select(n => n.Split(' ')[1]).Distinct().Count() > 90);
    }

    [Theory]
    [InlineData(1_500_000, 199_000, 7_200, 1, 3)]
    [InlineData(2_000_000, 199_000, 13_000, 1, 25)]
    [InlineData(1_000_000, 120_000, 13_000, 4, 6)]
    [InlineData(600_000, 51_000, 13_000, 1, 3)]
    public void BuildPlan_EveryBillIsFromTheMinimumToTheMax_AndAddsUpToTheTotal(
        long total, long maxBill, int rate24Kt, int minItems, int maxItems)
    {
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(total, maxBill, rate24Kt, "Cash", minItems, maxItems,
            start, start.AddDays(3), new[] { "22K", "18K" });
        // The dearest gram these masters can bill is 22K with 8 % wastage, and a bill can come
        // up to one such step short of its share.
        var dearestStep = rate24Kt * 1.00m * SyntheticBatchPlanLimits.WeightStep;

        for (var seed = 0; seed < 40; seed++)
        {
            var plan = new SyntheticBillPlanner().BuildPlan(req, DummyItems(), DummyKarats(), new Random(seed), null, Now);

            Assert.All(plan.Bills, b => Assert.InRange(b.Payload.Totals.GrandTotal, SyntheticBatchPlanLimits.MinBillTotal, maxBill));
            Assert.Equal(plan.Bills.Sum(b => b.Payload.Totals.GrandTotal), plan.TotalAmount);
            // What is not billed is the remainder under 50,000 and a step or less a bill. A bill
            // goes over its share only when the share is within a step of 50,000.
            Assert.InRange(plan.TotalAmount,
                total - (SyntheticBatchPlanLimits.MinBillTotal - 1) - plan.Bills.Count * dearestStep,
                total + plan.Bills.Count * dearestStep);
        }
    }

    [Fact]
    public void BuildPlan_LinesWeighAtLeastAGramInStepsOfAHundredth_AndArePricedAsTheInvoiceTabWould()
    {
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(3_000_000, 199_000, 13_000m, "Cash", 1, 25,
            start, start.AddDays(3), new[] { "22K", "18K" });
        var karats = DummyKarats().ToDictionary(k => k.Label);
        var items = DummyItems().ToDictionary(i => i.Name);

        var lines = new SyntheticBillPlanner()
            .BuildPlan(req, DummyItems(), DummyKarats(), new Random(5), null, Now)
            .Bills.SelectMany(b => b.Payload.Lines).ToList();

        Assert.Contains(lines, line => line.Quantity < 2m);
        Assert.All(lines, line =>
        {
            Assert.True(line.Quantity >= SyntheticBatchPlanLimits.MinLineWeight, $"{line.ItemName} weighs {line.Quantity} g");
            Assert.Equal(0m, line.Quantity % SyntheticBatchPlanLimits.WeightStep);
            Assert.Equal(line.Quantity, line.GrossWeight);
            Assert.Equal(ItemUnits.Gram, line.Unit);

            var item = items[line.ItemName];
            var priced = BillCalculator.ComputeLine(new BillCalculator.LineInputs(
                Rate24Kt: 13_000m, PurityPercent: karats[line.Karat!].PurityPercent, WastagePercent: item.WastagePercent,
                LabourPerUnit: item.DefaultLabourPerGram, NetWeight: line.Quantity, ExtraCharges: 0m,
                PricingMode: item.PricingMode, IsDiamond: false, DiamondRate: 0m));
            Assert.Equal(priced.EffectiveRate, line.Rate);
            Assert.Equal(priced.LineTotalInclusive, line.LineTotal);
        });
    }

    [Theory]
    [InlineData("22K Gold Chain", ItemCategories.GoldBased, ItemUnits.Gram, true)]
    [InlineData("Gold Bangle", ItemCategories.GoldBased, "", true)]
    [InlineData("Solitaire Ring", ItemCategories.Diamond, ItemUnits.Carat, false)]
    [InlineData("Solitaire Pendant", ItemCategories.Diamond, ItemUnits.Gram, false)]
    [InlineData("18K Diamond Earring", ItemCategories.GoldBased, ItemUnits.Gram, false)]
    [InlineData("Stone Ring", ItemCategories.GoldBased, ItemUnits.Gram, false)]
    [InlineData("Gemstone Pendant", ItemCategories.GoldBased, ItemUnits.Gram, false)]
    [InlineData("Ruby", ItemCategories.GoldBased, ItemUnits.Carat, false)]
    [InlineData("Gold Coin", ItemCategories.GoldBased, "mg", false)]
    // Item lists saved by older builds say "g"; v1.4 took only "gm" and left every such item out.
    [InlineData("Gold Chain", ItemCategories.GoldBased, "g", true)]
    [InlineData("Gold Chain", ItemCategories.GoldBased, "G", true)]
    [InlineData("Gold Chain", ItemCategories.GoldBased, " GM ", true)]
    [InlineData("Gold Chain", ItemCategories.GoldBased, "gms", true)]
    [InlineData("Gold Chain", ItemCategories.GoldBased, "Grams", true)]
    [InlineData("Gold Chain", ItemCategories.GoldBased, "gm.", true)]
    [InlineData("Gold Chain", ItemCategories.GoldBased, "cts", false)]
    public void MayBill_LeavesOutDiamondAndStoneItems(string name, string category, string unit, bool mayBill)
    {
        var item = new ItemMasterEntry(name, unit, 8m, category, PricingModes.Wastage, 0m, "");

        Assert.Equal(mayBill, SyntheticBillPlanner.MayBill(item));
    }

    [Fact]
    public void BuildPlan_AMaxOfTheMinimumMakesABillOfExactlyTheMinimum()
    {
        // At 10,000 a gram only 5.00 g of 22K chain comes to exactly 50,000; the other three item and
        // karat pairs land a step under it or over it, and that bill is priced again.
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(50_000, 50_000, 10_000m, "Cash", 1, 1,
            start, start.AddHours(1), new[] { "22K", "18K" });

        for (var seed = 0; seed < 10; seed++)
        {
            var bill = Assert.Single(new SyntheticBillPlanner().BuildPlan(req, DummyItems(), DummyKarats(), new Random(seed), null, Now).Bills);

            Assert.Equal(50_000m, bill.Payload.Totals.GrandTotal);
            var line = Assert.Single(bill.Payload.Lines);
            Assert.Equal(("22K Gold Chain", "22K", 5.00m), (line.ItemName, line.Karat, line.Quantity));
        }
    }

    [Fact]
    public void BuildPlan_NeverBillsADiamondOrStoneItem()
    {
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(2_000_000, 199_000, 13_000m, "Cash", 1, 6,
            start, start.AddDays(3), new[] { "22K" });
        var items = DummyItems().Concat(LeftOutItems()).ToList();

        var plan = new SyntheticBillPlanner().BuildPlan(req, items, DummyKarats(), new Random(3), null, Now);

        var billed = plan.Bills.SelectMany(b => b.Payload.Lines).Select(l => l.ItemName).Distinct().Order();
        Assert.Equal(DummyItems().Select(i => i.Name).Order(), billed);
    }

    [Fact]
    public void BuildPlan_IsRefusedWhenEveryItemIsDiamondOrStone()
    {
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(500_000, 199_000, 13_000m, "Cash", 1, 3,
            start, start.AddDays(1), new[] { "22K" });

        var refused = Assert.Throws<RequestValidationException>(() =>
            new SyntheticBillPlanner().BuildPlan(req, LeftOutItems(), DummyKarats(), new Random(1), null, Now));
        Assert.StartsWith("No item master can be used: a batch leaves out diamond and stone items", refused.Message);
    }

    [Fact]
    public void BuildPlan_BillsGoldItemsWhoseUnitIsWrittenG()
    {
        // The shape of an item list saved by an older build: gold items in "g", one item in "gm"
        // that is a stone item by name, and a diamond item in carats. v1.4 refused it outright.
        var items = new[]
        {
            new ItemMasterEntry("Gold Chain", "g", 8m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, ""),
            new ItemMasterEntry("Gold Bangle", "g", 0m, ItemCategories.GoldBased, PricingModes.Labour, 450m, ""),
            new ItemMasterEntry("Stone Ring", ItemUnits.Gram, 12m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, ""),
            new ItemMasterEntry("Solitaire", ItemUnits.Carat, 0m, ItemCategories.Diamond, PricingModes.Wastage, 0m, ""),
        };
        var start = new DateTimeOffset(2026, 4, 24, 9, 0, 0, TimeSpan.Zero);
        var req = new SyntheticBatchRequest(1_000_000, 199_000, 13_000m, "Cash", 1, 4,
            start, start.AddDays(1), new[] { "22K", "18K" });

        var plan = new SyntheticBillPlanner().BuildPlan(req, items, DummyKarats(), new Random(9), null, Now);

        var lines = plan.Bills.SelectMany(b => b.Payload.Lines).ToList();
        Assert.Equal(["Gold Bangle", "Gold Chain"], lines.Select(l => l.ItemName).Distinct().Order());
        Assert.All(lines, line => Assert.Equal("g", line.Unit));
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
            Name: "18K Gold Earring",
            Unit: ItemUnits.Gram,
            WastagePercent: 5m,
            ItemCategory: ItemCategories.GoldBased,
            PricingMode: PricingModes.Both,
            DefaultLabourPerGram: 200m,
            DefaultStockMappingLabel: "IIM Gold Jewellery 18KT"),
    };

    /// <summary>A diamond item, a gold-based item named for its diamonds or stones, and a carat item.</summary>
    private static IReadOnlyList<ItemMasterEntry> LeftOutItems() => new[]
    {
        new ItemMasterEntry("Solitaire Ring", ItemUnits.Carat, 0m, ItemCategories.Diamond, PricingModes.Wastage, 0m, ""),
        new ItemMasterEntry("18K Diamond Earring", ItemUnits.Gram, 5m, ItemCategories.GoldBased, PricingModes.Both, 200m, ""),
        new ItemMasterEntry("Stone Ring", ItemUnits.Gram, 12m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, ""),
        new ItemMasterEntry("Loose Ruby", ItemUnits.Carat, 0m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, ""),
    };

    private static IReadOnlyList<KaratMasterEntry> DummyKarats() => new[]
    {
        new KaratMasterEntry("22K", 92.0m, "Hallmarked Gold Jewellery 22KT"),
        new KaratMasterEntry("18K", 75.5m, "IIM Gold Jewellery 18KT"),
    };
}
