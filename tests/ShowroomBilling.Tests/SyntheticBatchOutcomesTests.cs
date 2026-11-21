using ShowroomBilling.Infrastructure.Bills;

namespace ShowroomBilling.Tests;

/// <summary>What the server keeps of how far a batch got, for a Desktop whose request had no answer.</summary>
public sealed class SyntheticBatchOutcomesTests
{
    [Fact]
    public void A_batch_is_counted_bill_by_bill_until_it_finishes()
    {
        var outcomes = new SyntheticBatchOutcomes();
        var batch = Guid.NewGuid();

        Assert.Null(outcomes.Find(batch));

        Assert.True(outcomes.TryStart(batch, plannedBillCount: 3));
        Assert.Equal((false, 3, 0, 0m), Counts(outcomes, batch));

        outcomes.Saved(batch, "A/1", 60_000m);
        outcomes.Saved(batch, "A/2", 70_000m);
        var running = outcomes.Find(batch)!;
        Assert.Equal((false, 3, 2, 130_000m), Counts(outcomes, batch));
        Assert.Equal(("A/1", "A/2"), (running.FirstInvoiceNumber, running.LastInvoiceNumber));

        outcomes.Finish(batch);
        Assert.Equal((true, 3, 2, 130_000m), Counts(outcomes, batch));
    }

    [Fact]
    public void The_same_batch_sent_twice_is_not_started_again()
    {
        var outcomes = new SyntheticBatchOutcomes();
        var batch = Guid.NewGuid();
        Assert.True(outcomes.TryStart(batch, 3));
        outcomes.Saved(batch, "A/1", 60_000m);

        Assert.False(outcomes.TryStart(batch, 5));

        Assert.Equal((false, 3, 1, 60_000m), Counts(outcomes, batch));
    }

    [Fact]
    public void Only_finished_batches_make_room_for_new_ones()
    {
        var outcomes = new SyntheticBatchOutcomes();
        var running = Guid.NewGuid();
        Assert.True(outcomes.TryStart(running, 4));
        var finished = Enumerable.Range(0, 30).Select(_ => Guid.NewGuid()).ToList();
        foreach (var batch in finished)
        {
            Assert.True(outcomes.TryStart(batch, 1));
            outcomes.Finish(batch);
        }

        // The one still being made is the one a Desktop will ask about, however many came after.
        Assert.NotNull(outcomes.Find(running));
        Assert.NotNull(outcomes.Find(finished[^1]));

        // Once it has finished it goes like the others.
        outcomes.Finish(running);
        Assert.True(outcomes.TryStart(Guid.NewGuid(), 1));
        Assert.Null(outcomes.Find(running));
        Assert.Null(outcomes.Find(finished[0]));
    }

    private static (bool Finished, int Planned, int Saved, decimal Total) Counts(SyntheticBatchOutcomes outcomes, Guid batch)
    {
        var outcome = outcomes.Find(batch)!;
        return (outcome.Finished, outcome.PlannedBillCount, outcome.BillCount, outcome.TotalAmount);
    }
}
