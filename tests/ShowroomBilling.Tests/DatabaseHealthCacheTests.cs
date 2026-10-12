using Microsoft.Extensions.Time.Testing;
using ShowroomBilling.Infrastructure.Health;

namespace ShowroomBilling.Tests;

public sealed class DatabaseHealthCacheTests
{
    private static readonly DatabaseHealthReading Answering = new(Reachable: true, Identity: "PROD", PendingMigrationCount: 0);
    private static readonly DatabaseHealthReading Silent = new(Reachable: false, Identity: null, PendingMigrationCount: 0);

    [Fact]
    public void A_reading_is_kept_for_five_seconds_and_no_longer()
    {
        var clock = new FakeTimeProvider();
        var cache = new DatabaseHealthCache(clock);

        Assert.False(cache.TryGet(out _));

        cache.Set(Answering);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(cache.TryGet(out var cached));
        Assert.Same(Answering, cached);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet(out _));
    }

    [Fact]
    public void A_database_that_did_not_answer_is_asked_again_after_the_same_five_seconds()
    {
        var clock = new FakeTimeProvider();
        var cache = new DatabaseHealthCache(clock);

        cache.Set(Silent);
        clock.Advance(TimeSpan.FromSeconds(6));

        Assert.False(cache.TryGet(out _));
    }

    [Fact]
    public void Storing_a_reading_hands_back_the_one_it_replaces_however_old()
    {
        // What lets the caller log "stopped answering" once and not on every poll.
        var clock = new FakeTimeProvider();
        var cache = new DatabaseHealthCache(clock);

        Assert.Null(cache.Set(Answering));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Same(Answering, cache.Set(Silent));
        Assert.Same(Silent, cache.Set(Silent));
    }
}
