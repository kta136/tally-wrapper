using ShowroomBilling.Application;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts;
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
        // Five bills or more hold 1,000,000 under the cap, so nothing is left unbilled.
        Assert.Equal(1_000_000, totals.Sum());
    }

    [Fact]
    public void PartitionBillTotals_GivesWhatIsLeftToTheBillsWithRoom_AndDropsOnlyWhatNoneHasRoomFor()
    {
        // 400,000 at most 199,000 a bill looks like three bills at least. With this seed the
        // first two are 192,536 and 189,573 and leave 17,891, which is under the smallest bill
        // there may be. The two have room for 15,891 of it, so both go to the cap and 2,000 is
        // not billed. (V1 dropped all 17,891.)
        var totals = SyntheticBillPlanner.PartitionBillTotals(400_000, 199_000, new Random(24));

        Assert.Equal([199_000L, 199_000L], totals);
    }

    [Fact]
    public void PartitionBillTotals_OverManySeeds_NeverExceedsTheCap_AndComesToTheTotalOrToEveryBillAtTheCap()
    {
        var fewest = int.MaxValue;
        var leftoverSpread = false;
        for (var seed = 0; seed < 20_000; seed++)
        {
            var totals = SyntheticBillPlanner.PartitionBillTotals(400_000, 199_000, new Random(seed));

            Assert.All(totals, t => Assert.InRange(t, SyntheticBatchPlanLimits.MinBillTotal, 199_000L));
            // All of it, unless the bills made cannot hold it: then every one of them is at the cap.
            Assert.Equal(Math.Min(400_000, totals.Count * 199_000L), totals.Sum());
            fewest = Math.Min(fewest, totals.Count);
            leftoverSpread |= totals.Count >= 3 && totals.Sum() == 400_000;
        }

        // More than 350,000 in bills of at most 199,000 each: never one bill, sometimes only two.
        Assert.Equal(2, fewest);
        Assert.True(leftoverSpread);
    }

    [Fact]
    public void PartitionBillTotals_DoesNotPutTheWholeLeftoverOnOneBill()
    {
        // The leftover goes to the bills in a random order, a random part each: over many batches
        // every position gets some of it, and it is not always one bill that takes it all.
        var raisedAt = new HashSet<int>();
        var sharedOut = false;
        for (var seed = 0; seed < 500; seed++)
        {
            var before = BillsBeforeTheLeftover(1_000_000, 199_000, new Random(seed));
            var after = SyntheticBillPlanner.PartitionBillTotals(1_000_000, 199_000, new Random(seed));

            Assert.Equal(before.Count, after.Count);
            var raised = Enumerable.Range(0, after.Count).Where(i => after[i] > before[i]).ToList();
            Assert.All(Enumerable.Range(0, after.Count), i => Assert.True(after[i] >= before[i]));
            raisedAt.UnionWith(raised);
            sharedOut |= raised.Count > 1;
        }

        Assert.True(sharedOut);
        Assert.Contains(0, raisedAt);
        Assert.True(raisedAt.Count >= 5, $"only positions {string.Join(", ", raisedAt.Order())} ever took any");
    }

    /// <summary>The bills as they are drawn, before what is left is handed out: the same draws from the same seed.</summary>
    private static List<long> BillsBeforeTheLeftover(long total, long max, Random rng)
    {
        var bills = new List<long>();
        var remaining = total;
        while (remaining >= SyntheticBatchPlanLimits.MinBillTotal)
        {
            var draw = rng.NextInt64(SyntheticBatchPlanLimits.MinBillTotal, Math.Min(max, remaining) + 1);
            bills.Add(draw);
            remaining -= draw;
        }

        return bills;
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
            earliestUtc: null,
            nowUtc: Now,
            rng: rng);

        Assert.Equal(30, slots.Count);
        for (var i = 1; i < slots.Count; i++)
            Assert.True(slots[i] > slots[i - 1], "slots must be strictly increasing");
        Assert.All(slots, t => Assert.InRange(t, start, end));
    }

    [Fact]
    public void BuildRandomSchedule_PutsNoBillBeforeTheEarliest()
    {
        var earliest = new DateTimeOffset(2026, 4, 24, 0, 15, 0, TimeSpan.Zero);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 5,
            startAtUtc: earliest.AddMinutes(5),
            endAtUtc: earliest.AddHours(1),
            earliestUtc: earliest,
            nowUtc: Now,
            rng: new Random(99));

        Assert.All(slots, t => Assert.True(t >= earliest, "slot must not be before the earliest"));
    }

    [Fact]
    public void BuildRandomSchedule_MayStartAMinuteBeforeTheEarliest_AndPutsNoBillThere()
    {
        // A counter built before 21 November 2026 offers the last bill's own time, to the second,
        // as the start: the minute before the earliest. That is allowed, and no bill goes in it.
        var earliest = new DateTimeOffset(2026, 4, 24, 12, 1, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 4, 24, 12, 0, 37, TimeSpan.Zero);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 3, startAtUtc: start, endAtUtc: earliest.AddMinutes(2), earliestUtc: earliest, nowUtc: Now, rng: new Random(1));

        Assert.Equal([earliest, earliest.AddMinutes(1), earliest.AddMinutes(2)], slots);
    }

    [Fact]
    public void BuildRandomSchedule_GivesUtcTimes_WhateverOffsetTheWindowCameWith()
    {
        var india = TimeSpan.FromMinutes(330);
        var start = new DateTimeOffset(2026, 4, 24, 10, 0, 0, india);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            billCount: 5, startAtUtc: start, endAtUtc: start.AddHours(1), earliestUtc: null, nowUtc: Now, rng: new Random(3));

        Assert.All(slots, slot =>
        {
            Assert.Equal(TimeSpan.Zero, slot.Offset);
            Assert.InRange(slot, start, start.AddHours(1));
        });
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenStartIsBeforeTheMinuteAheadOfTheEarliest()
    {
        var earliest = new DateTimeOffset(2026, 4, 24, 12, 1, 0, TimeSpan.Zero);

        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3,
                startAtUtc: earliest.AddSeconds(-61),
                endAtUtc: earliest.AddHours(1),
                earliestUtc: earliest,
                nowUtc: Now,
                rng: new Random(1)));
        // Said in India's time: 12:01 UTC is 17:31 there.
        Assert.Equal("Start time cannot be before 24 Apr 2026, 17:31, the first time after the last bill.", refused.Message);
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenTheWindowEndsAfterNow()
    {
        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3, startAtUtc: Now.AddHours(-1), endAtUtc: Now.AddMinutes(1), earliestUtc: null, nowUtc: Now, rng: new Random(1)));

        Assert.Equal("End time cannot be later than now.", refused.Message);
    }

    [Fact]
    public void BuildRandomSchedule_ThrowsWhenTheEarliestIsLaterThanNow()
    {
        // A batch made before 20 November 2026 could date bills later in the day than it was run,
        // and a bill dated ahead of the day it was saved counts from the start of its date.
        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                billCount: 3, startAtUtc: Now.AddHours(-1), endAtUtc: Now, earliestUtc: Now.AddHours(2), nowUtc: Now, rng: new Random(1)));

        Assert.Equal("The last bill leaves no time to date a batch in: a batch can be dated from 20 Nov 2026, 19:30, which is later than now.", refused.Message);
    }

    [Fact]
    public void EstimateBillCountBounds_CountsBillsOfTheMinimumToTheCap()
    {
        Assert.Equal((0, 0), SyntheticBillPlanner.EstimateBillCountBounds(0, 199_000));
        Assert.Equal((0, 0), SyntheticBillPlanner.EstimateBillCountBounds(49_999, 199_000));
        Assert.Equal((1, 1), SyntheticBillPlanner.EstimateBillCountBounds(50_000, 199_000));
        // Five bills of 1,99,000 leave 5,000, which is no bill of its own: five, not six.
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
            // The note is the payment, so a card bill's Tally narration does not say Cash (V1 did).
            Assert.Equal(expected, bill.Payload.Notes);
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
            // The batch comes to its total, or to every bill at the max when the bills made cannot
            // hold it (600,000 in eleven bills of at most 51,000), less a step or less a bill. A
            // bill goes over its share only when the share is within a step of 50,000.
            var held = Math.Min(total, plan.Bills.Count * maxBill);
            Assert.InRange(plan.TotalAmount,
                held - plan.Bills.Count * dearestStep,
                held + plan.Bills.Count * dearestStep);
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
            // Saved with the line, as the Invoice tab saves them: a bill opened for edit is then
            // priced by the mode it was made with, not by the item master as it is by then.
            Assert.Equal(item.PricingMode, line.PricingMode);
            Assert.Equal(ItemCategories.GoldBased, line.ItemCategory);
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

    // ── Shop hours (owner, 21 November 2026) ────────────────────────────────
    // All in India's time, and all before Now. 17 November 2026 is a Tuesday.

    private static readonly BatchHoursSettings TenToEight = new(new TimeOnly(10, 0), new TimeOnly(20, 0));

    [Fact]
    public void BuildRandomSchedule_WithShopHours_DatesNoBillOutsideThemOrOnTheClosedDay()
    {
        // Thursday the 12th to Thursday the 19th, shut on Tuesday the 17th: seven days of
        // 10:00 to 20:00, both minutes included, are 7 × 601 open minutes.
        var start = India(2026, 11, 12, 0, 0);
        var end = India(2026, 11, 19, 23, 59);
        var hours = TenToEight with { ClosedOn = DayOfWeek.Tuesday };
        Assert.Equal(7 * 601, BatchOpenMinutes.Count(start, end, null, hours));

        var days = new HashSet<DateOnly>();
        for (var seed = 0; seed < 20; seed++)
        {
            var slots = SyntheticBillPlanner.BuildRandomSchedule(300, start, end, null, Now, new Random(seed), hours);

            Assert.Equal(300, slots.Count);
            for (var i = 1; i < slots.Count; i++)
                Assert.True(slots[i] > slots[i - 1], "slots must be strictly increasing");
            Assert.All(slots, slot =>
            {
                Assert.Equal(TimeSpan.Zero, slot.Offset);
                Assert.InRange(TimeInIndia(slot), new TimeOnly(10, 0), new TimeOnly(20, 0));
                Assert.NotEqual(DayOfWeek.Tuesday, DayInIndia(slot));
                Assert.InRange(slot, start, end);
            });
            days.UnionWith(slots.Select(IndiaTime.DateOf));
        }

        // Every open day takes bills, the first and the last too.
        Assert.Equal(
            new[] { 12, 13, 14, 15, 16, 18, 19 }.Select(day => new DateOnly(2026, 11, day)),
            days.Order());
    }

    [Fact]
    public void BuildRandomSchedule_WithShopHours_UsesTheOpeningAndTheClosingMinute()
    {
        // Three open minutes a day over two days, and six bills: every open minute takes one.
        var hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(10, 2));

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            6, India(2026, 11, 12, 0, 0), India(2026, 11, 13, 23, 59), null, Now, new Random(4), hours);

        Assert.Equal(
            [
                India(2026, 11, 12, 10, 0), India(2026, 11, 12, 10, 1), India(2026, 11, 12, 10, 2),
                India(2026, 11, 13, 10, 0), India(2026, 11, 13, 10, 1), India(2026, 11, 13, 10, 2),
            ],
            slots);
    }

    [Fact]
    public void BuildRandomSchedule_WithShopHours_ReadsTheTimeAndTheWeekdayInIndia_NotInUtc()
    {
        // 04:00 to 07:00 in India is 22:30 to 01:30 UTC, across midnight there, and the window is
        // sent in UTC: 01:30 on Tuesday the 17th to 08:30 on Wednesday the 18th, India's time.
        // Tuesday is shut, so the only open minutes are Wednesday's, which begin at 22:30 on
        // Tuesday in UTC: a weekday or a time of day read in UTC would get this wrong.
        var start = new DateTimeOffset(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 11, 18, 3, 0, 0, TimeSpan.Zero);
        var hours = new BatchHoursSettings(new TimeOnly(4, 0), new TimeOnly(7, 0), DayOfWeek.Tuesday);
        Assert.Equal(181, BatchOpenMinutes.Count(start, end, null, hours));

        var datedOnTuesdayInUtc = false;
        for (var seed = 0; seed < 20; seed++)
        {
            var slots = SyntheticBillPlanner.BuildRandomSchedule(50, start, end, null, Now, new Random(seed), hours);

            Assert.Equal(50, slots.Distinct().Count());
            Assert.All(slots, slot =>
            {
                Assert.Equal(new DateOnly(2026, 11, 18), IndiaTime.DateOf(slot));
                Assert.InRange(TimeInIndia(slot), new TimeOnly(4, 0), new TimeOnly(7, 0));
            });
            datedOnTuesdayInUtc |= slots.Any(slot => slot.UtcDateTime.DayOfWeek == DayOfWeek.Tuesday);
        }

        // Half of Wednesday's open minutes are still Tuesday in UTC, and bills land there.
        Assert.True(datedOnTuesdayInUtc);
    }

    [Fact]
    public void BuildRandomSchedule_KeepsTheGap_WhenTheOpenMinutesHaveRoom()
    {
        // Two days of 10:00 to 20:00 are 1,202 open minutes: room for 81 bills a quarter of an
        // hour apart, so 40 keep the gap whatever the draw.
        var start = India(2026, 11, 12, 0, 0);
        var end = India(2026, 11, 13, 23, 59);
        var hours = TenToEight with { GapMinutes = 15 };
        Assert.Equal(81, BatchOpenMinutes.RoomAtGap(BatchOpenMinutes.Count(start, end, null, hours), 15));

        var seen = new HashSet<DateTimeOffset>();
        for (var seed = 0; seed < 50; seed++)
        {
            var slots = SyntheticBillPlanner.BuildRandomSchedule(40, start, end, null, Now, new Random(seed), hours);

            Assert.Equal(40, slots.Count);
            for (var i = 1; i < slots.Count; i++)
                Assert.True(slots[i] - slots[i - 1] >= TimeSpan.FromMinutes(15), $"seed {seed}: {slots[i - 1]:o} then {slots[i]:o}");
            Assert.All(slots, slot => Assert.InRange(TimeInIndia(slot), new TimeOnly(10, 0), new TimeOnly(20, 0)));
            seen.UnionWith(slots);
        }

        // Still drawn at random: the batches do not land on the same minutes.
        Assert.True(seen.Count > 600, $"only {seen.Count} different minutes over 50 batches");
    }

    [Fact]
    public void BuildRandomSchedule_WhenTheGapJustFits_PutsTheBillsExactlyThatFarApart()
    {
        // 10:00 to 10:30 is 31 open minutes: room for exactly three bills a quarter of an hour apart.
        var hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(10, 30), GapMinutes: 15);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            3, India(2026, 11, 12, 9, 0), India(2026, 11, 12, 11, 0), null, Now, new Random(8), hours);

        Assert.Equal([India(2026, 11, 12, 10, 0), India(2026, 11, 12, 10, 15), India(2026, 11, 12, 10, 30)], slots);
    }

    [Fact]
    public void BuildRandomSchedule_WhenTheGapDoesNotFit_PlansTheBatchWithTheLargestGapThatDoes()
    {
        // The same 31 minutes have no room for four bills 15 apart. The batch is made all the
        // same, with the largest gap that fits: 10 minutes, which leaves one placement.
        var hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(10, 30), GapMinutes: 15);
        var start = India(2026, 11, 12, 9, 0);
        var end = India(2026, 11, 12, 11, 0);

        var four = SyntheticBillPlanner.BuildRandomSchedule(4, start, end, null, Now, new Random(8), hours);
        Assert.Equal(
            [India(2026, 11, 12, 10, 0), India(2026, 11, 12, 10, 10), India(2026, 11, 12, 10, 20), India(2026, 11, 12, 10, 30)],
            four);

        // Five bills: 7 minutes apart is the most 31 minutes hold (8 would need 33).
        for (var seed = 0; seed < 50; seed++)
        {
            var five = SyntheticBillPlanner.BuildRandomSchedule(5, start, end, null, Now, new Random(seed), hours);
            for (var i = 1; i < five.Count; i++)
                Assert.True(five[i] - five[i - 1] >= TimeSpan.FromMinutes(7), $"seed {seed}: {five[i - 1]:o} then {five[i]:o}");
        }

        // As many bills as open minutes: no gap is left, and every minute takes a bill.
        var all = SyntheticBillPlanner.BuildRandomSchedule(31, start, end, null, Now, new Random(8), hours);
        Assert.Equal(Enumerable.Range(0, 31).Select(minute => India(2026, 11, 12, 10, 0).AddMinutes(minute)), all);
    }

    [Fact]
    public void BuildRandomSchedule_MeasuresTheGapAlongTheOpenMinutes_SoANightOnlyPutsBillsFurtherApart()
    {
        // 11 open minutes a day over two days are one line of 22. Four bills 7 apart fit it in one
        // way: its minutes 0, 7, 14 and 21, and 14 is the fourth minute of the second day. The
        // night between the second bill and the third counts for nothing on the line.
        var hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(10, 10), GapMinutes: 7);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            4, India(2026, 11, 12, 0, 0), India(2026, 11, 13, 23, 59), null, Now, new Random(2), hours);

        Assert.Equal(
            [India(2026, 11, 12, 10, 0), India(2026, 11, 12, 10, 7), India(2026, 11, 13, 10, 3), India(2026, 11, 13, 10, 10)],
            slots);
    }

    [Fact]
    public void BuildRandomSchedule_KeepsAGap_WithNoHoursSet()
    {
        var start = India(2026, 11, 12, 9, 0);

        var slots = SyntheticBillPlanner.BuildRandomSchedule(
            5, start, start.AddMinutes(40), null, Now, new Random(6), new BatchHoursSettings(GapMinutes: 10));

        Assert.Equal(Enumerable.Range(0, 5).Select(i => start.AddMinutes(10 * i)), slots);
    }

    [Fact]
    public void BuildRandomSchedule_IsRefusedWhenNoMinuteOfTheWindowIsOpen()
    {
        // The whole window is on the closed day.
        var closedDay = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                3, India(2026, 11, 17, 9, 0), India(2026, 11, 17, 21, 0), null, Now, new Random(1),
                TenToEight with { ClosedOn = DayOfWeek.Tuesday }));
        Assert.Equal(
            "The shop is not open at any minute of the selected times (shop hours: 10:00 to 20:00, closed on Tuesday). A batch dates bills only while the shop is open; the hours are set in Settings → Admin.",
            closedDay.Message);

        // The whole window is after closing time.
        var afterHours = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                3, India(2026, 11, 12, 20, 1), India(2026, 11, 12, 23, 0), null, Now, new Random(1), TenToEight));
        Assert.Equal(
            "The shop is not open at any minute of the selected times (shop hours: 10:00 to 20:00). A batch dates bills only while the shop is open; the hours are set in Settings → Admin.",
            afterHours.Message);

        // A closed day and no hours.
        var noHours = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(
                3, India(2026, 11, 17, 9, 0), India(2026, 11, 17, 21, 0), null, Now, new Random(1),
                new BatchHoursSettings(ClosedOn: DayOfWeek.Tuesday)));
        Assert.StartsWith(
            "The shop is not open at any minute of the selected times (shop hours: any time of day, closed on Tuesday).",
            noHours.Message);
    }

    [Fact]
    public void BuildRandomSchedule_IsRefusedWhenTheOpenMinutesAreFewerThanTheBills()
    {
        var hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(10, 2));
        var start = India(2026, 11, 12, 0, 0);

        var refused = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(5, start, start.AddHours(23), null, Now, new Random(1), hours));
        Assert.Equal(
            "The selected times have 3 minute(s) inside the shop hours (10:00 to 10:02); 5 bills need one each. Choose a longer time, or change the hours in Settings → Admin.",
            refused.Message);

        // With no hours the refusal is worded as it was.
        var plain = Assert.Throws<RequestValidationException>(() =>
            SyntheticBillPlanner.BuildRandomSchedule(5, start, start.AddMinutes(2), null, Now, new Random(1)));
        Assert.Equal("Selected window has 3 minute slot(s); 5 are required.", plain.Message);
    }

    [Fact]
    public void BuildRandomSchedule_WithNoHoursAndNoGap_IsWhatItWasBeforeThereWereHours()
    {
        var start = new DateTimeOffset(2026, 4, 24, 0, 0, 0, TimeSpan.Zero);
        foreach (var (bills, minutes) in new[] { (30, 120), (5, 4_000), (100, 110) })
        {
            for (var seed = 0; seed < 20; seed++)
            {
                var before = ScheduleAsItWas(bills, start, start.AddMinutes(minutes), new Random(seed));

                Assert.Equal(before, SyntheticBillPlanner.BuildRandomSchedule(bills, start, start.AddMinutes(minutes), null, Now, new Random(seed)));
                Assert.Equal(before, SyntheticBillPlanner.BuildRandomSchedule(bills, start, start.AddMinutes(minutes), null, Now, new Random(seed), hours: null));
                // Hours saved with nothing in them, and a gap of one minute, change nothing either.
                Assert.Equal(before, SyntheticBillPlanner.BuildRandomSchedule(bills, start, start.AddMinutes(minutes), null, Now, new Random(seed), new BatchHoursSettings()));
                Assert.Equal(before, SyntheticBillPlanner.BuildRandomSchedule(bills, start, start.AddMinutes(minutes), null, Now, new Random(seed), new BatchHoursSettings(GapMinutes: 1)));
            }
        }
    }

    /// <summary>
    /// The schedule as it was drawn before there were shop hours: distinct minutes of the window
    /// picked from the same seed in the same way, sorted.
    /// </summary>
    private static List<DateTimeOffset> ScheduleAsItWas(int bills, DateTimeOffset start, DateTimeOffset end, Random rng)
    {
        var slots = (int)(end - start).TotalMinutes + 1;
        var chosen = new List<int>();
        if ((long)bills * 8 <= slots)
        {
            var picked = new HashSet<int>();
            while (picked.Count < bills)
            {
                var candidate = rng.Next(slots);
                if (picked.Add(candidate)) chosen.Add(candidate);
            }
        }
        else
        {
            var indices = Enumerable.Range(0, slots).ToArray();
            for (var i = 0; i < bills; i++)
            {
                var j = rng.Next(i, slots);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            chosen.AddRange(indices.Take(bills));
        }

        return chosen.Order().Select(minute => start.AddMinutes(minute)).ToList();
    }

    [Fact]
    public void BuildPlan_DatesEveryBillInsideTheShopHours()
    {
        var start = India(2026, 11, 12, 0, 0);
        var req = new SyntheticBatchRequest(1_500_000, 199_000, 7200m, "Cash", 1, 2,
            start, India(2026, 11, 19, 23, 59), new[] { "22K" });
        var hours = TenToEight with { ClosedOn = DayOfWeek.Tuesday, GapMinutes = 30 };

        var plan = new SyntheticBillPlanner().BuildPlan(req, DummyItems(), DummyKarats(), new Random(12), null, Now, hours);

        Assert.True(plan.Bills.Count >= 8);
        Assert.All(plan.Bills, bill =>
        {
            Assert.InRange(TimeInIndia(bill.ScheduledAtUtc), new TimeOnly(10, 0), new TimeOnly(20, 0));
            Assert.NotEqual(DayOfWeek.Tuesday, DayInIndia(bill.ScheduledAtUtc));
            // The bill's date is the day in India it is dated at.
            Assert.Equal(IndiaTime.DateOf(bill.ScheduledAtUtc), bill.Payload.BillDate);
        });
        for (var i = 1; i < plan.Bills.Count; i++)
            Assert.True(plan.Bills[i].ScheduledAtUtc - plan.Bills[i - 1].ScheduledAtUtc >= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void BatchOpenMinutes_Count_CountsWholeMinutesOfTheWindow()
    {
        var ten = India(2026, 11, 12, 10, 0);

        // No hours: every whole minute from the start to the end, both included.
        Assert.Equal(11, BatchOpenMinutes.Count(ten, ten.AddMinutes(10), null, null));
        // A start part-way through a minute counts from the next one; an end part-way counts its minute.
        Assert.Equal(10, BatchOpenMinutes.Count(ten.AddSeconds(30), ten.AddMinutes(10), null, null));
        Assert.Equal(11, BatchOpenMinutes.Count(ten, ten.AddMinutes(10).AddSeconds(59), null, null));
        // Nothing before the earliest time, and an earliest time before the start changes nothing.
        Assert.Equal(6, BatchOpenMinutes.Count(ten, ten.AddMinutes(10), ten.AddMinutes(5), null));
        Assert.Equal(11, BatchOpenMinutes.Count(ten, ten.AddMinutes(10), ten.AddMinutes(-5), null));
        Assert.Equal(0, BatchOpenMinutes.Count(ten, ten.AddMinutes(10), ten.AddMinutes(11), null));
        // No window, no minutes.
        Assert.Equal(0, BatchOpenMinutes.Count(ten, ten, null, null));
        Assert.Equal(0, BatchOpenMinutes.Count(ten.AddMinutes(1), ten, null, null));
        // Half a minute that holds no whole one.
        Assert.Equal(0, BatchOpenMinutes.Count(ten.AddSeconds(10), ten.AddSeconds(40), null, null));
    }

    [Fact]
    public void BatchOpenMinutes_Count_KeepsToTheHoursAndTheClosedDay()
    {
        // A whole day of 10:00 to 20:00: both minutes included.
        Assert.Equal(601, BatchOpenMinutes.Count(India(2026, 11, 12, 0, 0), India(2026, 11, 12, 23, 59), null, TenToEight));
        // A window that starts inside the hours and ends inside the next day's: 15:30 to 20:00
        // is 271 minutes, 10:00 to 12:00 is 121.
        Assert.Equal(271 + 121, BatchOpenMinutes.Count(India(2026, 11, 12, 15, 30), India(2026, 11, 13, 12, 0), null, TenToEight));
        // Wholly inside the hours of one day.
        Assert.Equal(46, BatchOpenMinutes.Count(India(2026, 11, 12, 11, 0), India(2026, 11, 12, 11, 45), null, TenToEight));
        // Starts before opening and ends at the opening minute: that one minute.
        Assert.Equal(1, BatchOpenMinutes.Count(India(2026, 11, 12, 6, 0), India(2026, 11, 12, 10, 0), null, TenToEight));
        // Starts at the closing minute.
        Assert.Equal(1, BatchOpenMinutes.Count(India(2026, 11, 12, 20, 0), India(2026, 11, 12, 23, 0), null, TenToEight));
        // Between closing and the next opening: none.
        Assert.Equal(0, BatchOpenMinutes.Count(India(2026, 11, 12, 20, 1), India(2026, 11, 13, 9, 59), null, TenToEight));
        // The earliest time inside the hours cuts the first day short.
        Assert.Equal(121 + 601, BatchOpenMinutes.Count(
            India(2026, 11, 12, 0, 0), India(2026, 11, 13, 23, 59), India(2026, 11, 12, 18, 0), TenToEight));

        // A week has six open days when one is shut, with hours and without.
        var week = (Start: India(2026, 11, 9, 0, 0), End: India(2026, 11, 15, 23, 59));
        Assert.Equal(6 * 601, BatchOpenMinutes.Count(week.Start, week.End, null, TenToEight with { ClosedOn = DayOfWeek.Sunday }));
        Assert.Equal(6 * 1440, BatchOpenMinutes.Count(week.Start, week.End, null, new BatchHoursSettings(ClosedOn: DayOfWeek.Sunday)));
        Assert.Equal(7 * 1440, BatchOpenMinutes.Count(week.Start, week.End, null, new BatchHoursSettings()));

        // One time without the other is no hours: any time of day.
        Assert.Equal(1440, BatchOpenMinutes.Count(
            India(2026, 11, 12, 0, 0), India(2026, 11, 12, 23, 59), null, new BatchHoursSettings(Opens: new TimeOnly(10, 0))));
        // The gap does not change what is open.
        Assert.Equal(601, BatchOpenMinutes.Count(
            India(2026, 11, 12, 0, 0), India(2026, 11, 12, 23, 59), null, TenToEight with { GapMinutes = 45 }));
    }

    [Fact]
    public void BatchOpenMinutes_Count_IsWhatCountingMinuteByMinuteGives()
    {
        var rng = new Random(31);
        var from = India(2026, 10, 1, 0, 0);
        for (var round = 0; round < 60; round++)
        {
            var start = from.AddSeconds(rng.Next(0, 20 * 24 * 3600));
            var end = start.AddSeconds(rng.Next(1, 12 * 24 * 3600));
            DateTimeOffset? earliest = rng.Next(3) == 0 ? start.AddSeconds(rng.Next(-3600, 2 * 24 * 3600)) : null;
            var opens = new TimeOnly(rng.Next(0, 12), rng.Next(0, 60));
            var hours = new BatchHoursSettings(
                Opens: rng.Next(4) == 0 ? null : opens,
                Closes: rng.Next(4) == 0 ? null : opens.AddMinutes(rng.Next(1, 600)),
                ClosedOn: rng.Next(2) == 0 ? null : (DayOfWeek)rng.Next(7));

            Assert.Equal(CountMinuteByMinute(start, end, earliest, hours), BatchOpenMinutes.Count(start, end, earliest, hours));
        }
    }

    private static int CountMinuteByMinute(DateTimeOffset start, DateTimeOffset end, DateTimeOffset? earliest, BatchHoursSettings hours)
    {
        var from = earliest is { } e && e > start ? e : start;
        var minute = new DateTimeOffset(from.Ticks - from.Ticks % TimeSpan.TicksPerMinute, from.Offset);
        if (minute < from) minute = minute.AddMinutes(1);

        var count = 0;
        for (; minute <= end; minute = minute.AddMinutes(1))
        {
            var time = TimeInIndia(minute);
            if (hours is { Opens: { } opens, Closes: { } closes } && (time < opens || time > closes)) continue;
            if (hours.ClosedOn == DayInIndia(minute)) continue;
            count++;
        }

        return count;
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(-3, 5, 0)]
    [InlineData(1, 5, 1)]
    [InlineData(5, 5, 1)]
    [InlineData(6, 5, 2)]
    [InlineData(30, 15, 2)]
    [InlineData(31, 15, 3)]
    [InlineData(601, 15, 41)]
    // No gap: a bill a minute.
    [InlineData(31, 0, 31)]
    [InlineData(31, 1, 31)]
    public void BatchOpenMinutes_RoomAtGap_IsTheBillsThatFitThatFarApart(int openMinutes, int gapMinutes, int room)
    {
        Assert.Equal(room, BatchOpenMinutes.RoomAtGap(openMinutes, gapMinutes));
    }

    private static DateTimeOffset India(int year, int month, int day, int hour, int minute)
        => new(year, month, day, hour, minute, 0, IndiaTime.Offset);

    private static TimeOnly TimeInIndia(DateTimeOffset instant)
        => TimeOnly.FromTimeSpan(instant.ToOffset(IndiaTime.Offset).TimeOfDay);

    private static DayOfWeek DayInIndia(DateTimeOffset instant)
        => instant.ToOffset(IndiaTime.Offset).DayOfWeek;

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
