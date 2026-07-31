using Halo.ClaudeCode;

namespace Halo.Tests;

// Two sessions can be waiting at once. One banner at a time, FIFO, and an expired head must not sit
// there blocking a live question behind it - that is the failure that would make the queue worse than
// no queue at all.
public sealed class AskQueueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static PendingAsk Ask(string nonce, DateTimeOffset expires) =>
        new(nonce, 1, "sess", "AskUserQuestion", null, "Which?",
            [new AskOption("A", "a"), new AskOption("B", "b")], expires);

    [Fact]
    public void EmptyQueueHasNoHead() => Assert.Null(new AskQueue().Head(T0));

    [Fact]
    public void HeadIsTheFirstSeen()
    {
        var q = new AskQueue();
        q.Observe(Ask("first", T0.AddSeconds(20)));
        q.Observe(Ask("second", T0.AddSeconds(20)));
        Assert.Equal("first", q.Head(T0)!.Nonce);
    }

    [Fact]
    public void AnsweringTheHeadPromotesTheNext()
    {
        var q = new AskQueue();
        q.Observe(Ask("first", T0.AddSeconds(20)));
        q.Observe(Ask("second", T0.AddSeconds(20)));
        q.Remove("first");
        Assert.Equal("second", q.Head(T0)!.Nonce);
    }

    // the one that matters: a question nobody answered must not hold the banner hostage
    [Fact]
    public void ExpiredHeadDoesNotBlockTheNext()
    {
        var q = new AskQueue();
        q.Observe(Ask("stale", T0.AddSeconds(5)));
        q.Observe(Ask("live", T0.AddSeconds(60)));
        Assert.Equal("live", q.Head(T0.AddSeconds(10))!.Nonce);
    }

    [Fact]
    public void EverythingExpiredMeansNoBanner()
    {
        var q = new AskQueue();
        q.Observe(Ask("a", T0.AddSeconds(5)));
        q.Observe(Ask("b", T0.AddSeconds(5)));
        Assert.Null(q.Head(T0.AddSeconds(10)));
    }

    // the directory is rescanned on every poll, so the same ask arrives again and again
    [Fact]
    public void ObservingTheSameNonceTwiceKeepsOneEntry()
    {
        var q = new AskQueue();
        q.Observe(Ask("dup", T0.AddSeconds(20)));
        q.Observe(Ask("dup", T0.AddSeconds(20)));
        Assert.Equal(1, q.Count);
    }

    // re-observing must not reorder: a rescan that reshuffled the queue would swap the banner under the
    // user's cursor between one poll and the next
    [Fact]
    public void ReObservingDoesNotChangeOrder()
    {
        var q = new AskQueue();
        q.Observe(Ask("first", T0.AddSeconds(20)));
        q.Observe(Ask("second", T0.AddSeconds(20)));
        q.Observe(Ask("first", T0.AddSeconds(20)));
        Assert.Equal("first", q.Head(T0)!.Nonce);
    }

    [Fact]
    public void SweepReportsAndDropsExpired()
    {
        var q = new AskQueue();
        q.Observe(Ask("stale", T0.AddSeconds(5)));
        q.Observe(Ask("live", T0.AddSeconds(60)));

        var dropped = q.Sweep(T0.AddSeconds(10));

        Assert.Equal(["stale"], dropped);
        Assert.Equal(1, q.Count);
        Assert.Equal("live", q.Head(T0.AddSeconds(10))!.Nonce);
    }

    [Fact]
    public void RemovingSomethingAbsentIsHarmless()
    {
        var q = new AskQueue();
        q.Observe(Ask("only", T0.AddSeconds(20)));
        q.Remove("never-existed");
        Assert.Equal(1, q.Count);
    }
}
