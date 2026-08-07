using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Halo.Notifications;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// The orderings these cover all have the same shape: a battery read that started earlier finishes
/// later, after the pill has legitimately moved on. Every one of them passes a happy-path trigger
/// test and then leaves a phantom device on screen in real use, so they are pinned here rather than
/// argued about. Nothing below touches Bluetooth: reads are gates the test opens by hand.
/// </summary>
public class BtCoordinatorTests
{
    /// <summary>
    /// Battery reads as gates. A read never completes until the test says so, and continuations run
    /// inline, so "complete this read" and "the coordinator has finished reacting" are one step.
    /// </summary>
    private sealed class Reads
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<TaskCompletionSource<int>>> _gates = new(StringComparer.Ordinal);

        public Task<int> Read(BtDevice dev)
        {
            var gate = new TaskCompletionSource<int>();
            lock (_lock)
            {
                if (!_gates.TryGetValue(dev.Id, out var list)) _gates[dev.Id] = list = new();
                list.Add(gate);
            }
            return gate.Task;
        }

        /// <summary>Completes the oldest read for <paramref name="id"/> that is still in flight.</summary>
        public void Complete(string id, int pct)
        {
            TaskCompletionSource<int>? gate = null;
            lock (_lock)
                if (_gates.TryGetValue(id, out var list))
                    foreach (var g in list)
                        if (!g.Task.IsCompleted) { gate = g; break; }
            Assert.True(gate is not null, $"no battery read in flight for {id}");
            gate!.SetResult(pct);
        }
    }

    private static BtDevice Dev(string id, string? name = null) => new(id, name ?? id, null);

    private static (BtCoordinator coord, Reads reads, List<BtSnapshot> published) Build()
    {
        var reads = new Reads();
        var published = new List<BtSnapshot>();
        var coord = new BtCoordinator(
            reads.Read,
            snap => { lock (published) published.Add(snap); },
            retryAfter: TimeSpan.Zero,
            delay: _ => Task.CompletedTask);
        return (coord, reads, published);
    }

    /// <summary>Connects a device and lets its read succeed, leaving it featured.</summary>
    private static async Task Feature(BtCoordinator coord, Reads reads, string id, int pct)
    {
        var t = coord.Added(Dev(id), flash: true);
        reads.Complete(id, pct);
        await t;
        Assert.Equal(id, coord.FeaturedId);
    }

    [Fact]
    public async Task SlowRead_CannotOverwriteADeviceThatConnectedLaterAndReadFaster()
    {
        var (coord, reads, published) = Build();

        var slow = coord.Added(Dev("A"), flash: true);
        var fast = coord.Added(Dev("B"), flash: true);

        reads.Complete("B", 50);
        reads.Complete("A", 90);
        await Task.WhenAll(slow, fast);

        Assert.Equal("B", coord.FeaturedId);
        Assert.Single(published);
        Assert.Equal("B", published[0].Id);
    }

    [Fact]
    public async Task StaleHandoff_CannotReplaceADeviceFeaturedWhileItWasReading()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "B", 70);
        await Feature(coord, reads, "A", 80);

        // A drops; the handoff starts reading B and parks there.
        int mark = published.Count;
        var handoff = coord.Removed("A");

        // C arrives and legitimately takes the pill while that read is still in flight.
        await Feature(coord, reads, "C", 60);

        reads.Complete("B", 55);
        await handoff;

        Assert.Equal("C", coord.FeaturedId);
        Assert.Equal("C", published[^1].Id);
        Assert.DoesNotContain(published.GetRange(mark, published.Count - mark), s => s.Id == "B");
    }

    [Fact]
    public async Task CandidateThatDisconnectsDuringItsRead_IsNeverPublished()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "B", 70);
        await Feature(coord, reads, "A", 80);

        int mark = published.Count;
        var handoff = coord.Removed("A");
        await coord.Removed("B");     // B leaves while its own battery is being read
        reads.Complete("B", 55);
        await handoff;

        Assert.DoesNotContain(published.GetRange(mark, published.Count - mark), s => s.Id == "B");
        Assert.Null(coord.FeaturedId);
        Assert.False(published[^1].Connected);
    }

    [Fact]
    public async Task ExhaustedHandoff_CannotTouchADeviceFeaturedAfterItStarted()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "B", 70);
        await Feature(coord, reads, "A", 80);

        var handoff = coord.Removed("A");
        await Feature(coord, reads, "C", 60);

        // The only candidate has no readable battery, so the old handoff runs out of readings and
        // would fall through to showing it as unknown -- over C, which arrived later and won.
        reads.Complete("B", -1);
        await handoff;

        Assert.Equal("C", coord.FeaturedId);
        Assert.DoesNotContain(published, s => !s.Connected);
        Assert.Equal("C", published[^1].Id);
    }

    [Fact]
    public void ClearingThePreview_LeavesADeviceThatSupersededItAlone()
    {
        var (coord, reads, published) = Build();

        coord.Preview(Dev("halo:bt-test", "AirPods Pro"), 72);
        // A real device connects and legitimately takes the pill from the preview.
        var added = coord.Added(Dev("A"), flash: true);
        reads.Complete("A", 55);
        Assert.Equal("A", coord.FeaturedId);

        coord.RemovePreview("halo:bt-test");

        // The preview is gone from the connected set, but it was not what was on screen.
        Assert.Equal("A", coord.FeaturedId);
        Assert.True(published[^1].Connected);
        Assert.True(added.IsCompleted);
    }

    [Fact]
    public async Task DisconnectOfTheFeaturedDevice_HandsOverToTheNextConnectedOne()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "B", 70);
        await Feature(coord, reads, "A", 80);

        var handoff = coord.Removed("A");
        reads.Complete("B", 55);
        await handoff;

        Assert.Equal("B", coord.FeaturedId);
        Assert.Equal("B", published[^1].Id);
        // A handoff is a fallback, not an arrival: it must not grab focus.
        Assert.False(published[^1].Flash);
    }

    [Fact]
    public async Task DisconnectWithNothingLeft_ClearsThePill()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "A", 80);

        await coord.Removed("A");

        Assert.Null(coord.FeaturedId);
        Assert.False(published[^1].Connected);
    }

    [Fact]
    public async Task DeviceThatVanishesDuringItsOwnRead_IsNeverFeatured()
    {
        var (coord, reads, published) = Build();

        var added = coord.Added(Dev("A"), flash: true);
        await coord.Removed("A");
        reads.Complete("A", 80);
        await added;

        Assert.Null(coord.FeaturedId);
        Assert.Empty(published);
    }

    [Fact]
    public async Task ADeviceWithNoReadableBattery_IsStillFeaturedWithTheNumberWithheld()
    {
        var (coord, reads, published) = Build();

        var added = coord.Added(Dev("A"), flash: true);
        reads.Complete("A", -1);      // first attempt
        reads.Complete("A", -1);      // and the retry
        await added;

        Assert.Equal("A", coord.FeaturedId);
        Assert.True(published[^1].Connected);
        Assert.False(published[^1].PctKnown);
    }

    [Fact]
    public async Task HandoffPrefersACandidateWhoseBatteryCanBeRead()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "unreadable", 70);
        await Feature(coord, reads, "readable", 70);
        await Feature(coord, reads, "A", 80);

        var handoff = coord.Removed("A");
        reads.Complete("readable", -1);     // most recent candidate, but no reading
        reads.Complete("unreadable", 45);   // older candidate, readable
        await handoff;

        Assert.Equal("unreadable", coord.FeaturedId);
        Assert.Equal(45, published[^1].Pct);
    }

    [Fact]
    public async Task HandoffWithNoReadableCandidate_ShowsTheMostRecentOneAsUnknown()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "older", 70);
        await Feature(coord, reads, "newer", 70);
        await Feature(coord, reads, "A", 80);

        var handoff = coord.Removed("A");
        reads.Complete("newer", -1);
        reads.Complete("older", -1);
        await handoff;

        // Devices are still connected, so blanking the pill would be the bigger lie.
        Assert.Equal("newer", coord.FeaturedId);
        Assert.True(published[^1].Connected);
        Assert.False(published[^1].PctKnown);
    }

    [Fact]
    public async Task ARefreshThatCannotRead_KeepsTheNumberItAlreadyHas()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "A", 80);
        int before = published.Count;

        var refresh = coord.RefreshFeatured();
        reads.Complete("A", -1);
        await refresh;

        // One unlucky read is not evidence the battery is gone; staleness handles that.
        Assert.Equal(before, published.Count);
        Assert.Equal(80, published[^1].Pct);
    }

    [Fact]
    public async Task RefreshOfTheFeaturedDevice_UpdatesTheNumberWithoutReflashing()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "A", 80);

        var refresh = coord.RefreshFeatured();
        reads.Complete("A", 64);
        await refresh;

        Assert.Equal(64, published[^1].Pct);
        Assert.False(published[^1].Flash);
        Assert.True(published[^1].Revision > published[0].Revision);
    }

    [Fact]
    public async Task RefreshThatLandsAfterTheDeviceChanged_IsDiscarded()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "A", 80);

        var refresh = coord.RefreshFeatured();
        await Feature(coord, reads, "C", 60);
        reads.Complete("A", 10);
        await refresh;

        Assert.Equal("C", published[^1].Id);
        Assert.Equal(60, published[^1].Pct);
    }

    [Fact]
    public void PreviewDevice_CanBeShownAndTakenBack()
    {
        var (coord, _, published) = Build();

        coord.Preview(Dev("halo:bt-test", "AirPods Pro"), 72);
        Assert.Equal("halo:bt-test", coord.FeaturedId);
        Assert.Equal(72, published[^1].Pct);

        coord.RemovePreview("halo:bt-test");
        Assert.Null(coord.FeaturedId);
        Assert.False(published[^1].Connected);
    }

    [Fact]
    public async Task PublishedRevisions_OnlyEverIncrease()
    {
        var (coord, reads, published) = Build();
        await Feature(coord, reads, "B", 70);
        await Feature(coord, reads, "A", 80);
        var handoff = coord.Removed("A");
        reads.Complete("B", 55);
        await handoff;

        for (int i = 1; i < published.Count; i++)
            Assert.True(published[i].Revision > published[i - 1].Revision,
                $"revision went backwards at {i}");
    }
}
