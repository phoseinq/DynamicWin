using System;
using Halo.Update;
using Xunit;

namespace Halo.Tests;

// Most machines running this are offline most of the time, and offline is the normal case for a user who
// installed from a downloaded setup. A failed check must therefore back off rather than turn into a poll.
public class AutoUpdateTests
{
    [Fact]
    public void A_successful_check_waits_a_day()
        => Assert.Equal(TimeSpan.FromHours(24), AutoUpdate.Wait(0));

    [Theory]
    [InlineData(1, 30)]      // first failure: try again soon, the network may just have blinked
    [InlineData(2, 6 * 60)]
    [InlineData(3, 12 * 60)]
    [InlineData(4, 24 * 60)]
    public void Each_failure_waits_longer_than_the_last(int fails, int minutes)
        => Assert.Equal(TimeSpan.FromMinutes(minutes), AutoUpdate.Wait(fails));

    // A machine that is offline for a week must not climb past a day and must not overflow the ladder
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(400)]
    public void The_ladder_settles_at_a_day_and_never_runs_off_the_end(int fails)
        => Assert.Equal(TimeSpan.FromHours(24), AutoUpdate.Wait(fails));

    // A four-part AssemblyVersion against a three-part tag is the trap: parsed "3.1.0" has Revision -1 and
    // so compares as LESS than 3.1.0.0, which would either call the running build outdated forever or
    // never offer a genuine update.
    [Theory]
    [InlineData("v3.1.0", "3.1.0.0", false)]   // the same release must not reinstall itself in a loop
    [InlineData("v3.0.2", "3.1.0.0", false)]   // an older release is never offered
    [InlineData("v3.1.1", "3.1.0.0", true)]
    [InlineData("v3.2.0", "3.1.0.0", true)]
    [InlineData("v4.0.0", "3.1.0.0", true)]
    [InlineData("3.2.0", "3.1.0.0", true)]     // tags without the v prefix
    [InlineData("V3.2.0", "3.1.0.0", true)]
    public void Only_a_genuinely_higher_release_counts_as_newer(string tag, string current, bool expected)
        => Assert.Equal(expected, AutoUpdate.IsNewer(tag, Version.Parse(current)));

    // a release named something we cannot read must never be treated as an update to install
    [Theory]
    [InlineData("nightly")]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("2026-07-26")]
    public void An_unreadable_tag_is_never_newer(string tag)
        => Assert.False(AutoUpdate.IsNewer(tag, new Version(3, 1, 0, 0)));

    [Fact]
    public void The_ladder_never_shortens_as_failures_pile_up()
    {
        var last = TimeSpan.Zero;
        for (int f = 1; f <= 10; f++)
        {
            var w = AutoUpdate.Wait(f);
            Assert.True(w >= last, $"{f} failures waited {w}, less than the previous {last}");
            last = w;
        }
    }
}
