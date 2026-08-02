using Halo.Shell;

namespace Halo.Tests;

// The alerts Halo raises about the machine itself. Each of these used to be a single latched flag, which
// is how a laptop at 6% stayed silent (it had already said 19%) and how an outright outage arrived, if at
// all, wearing the words and the tile of a slow connection.
public sealed class LocalAlertTests
{
    [Theory]
    [InlineData(100, -1)]
    [InlineData(21, -1)]
    [InlineData(20, 0)]     // the rung itself is already low, not merely approaching it
    [InlineData(11, 0)]
    [InlineData(10, 1)]
    [InlineData(0, 1)]
    public void BatteryRungs(int pct, int expected) => Assert.Equal(expected, NotchController.BatteryTier(pct));

    // the second banner is the point: 19% and then 6% are two different pieces of news, and the tier only
    // ever climbs while unplugged, so it is two banners and never a stream of them
    [Fact]
    public void CriticalOutranksLow()
    {
        Assert.True(NotchController.BatteryTier(6) > NotchController.BatteryTier(19));
        Assert.Equal(NotchController.BatteryTier(19), NotchController.BatteryTier(13));
    }

    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(false, false, true, "slow")]
    [InlineData(false, true, false, "api")]
    [InlineData(true, true, false, "offline")]     // NetMon only asserts NetDown alongside ApiDown
    [InlineData(true, true, true, "offline")]
    public void WorstTroubleWins(bool netDown, bool apiDown, bool slow, string? expected)
        => Assert.Equal(expected, NotchController.NetTrouble(netDown, apiDown, slow));

    // with nothing reaching the internet, "their API is unreachable" is true and useless - the router is
    // the thing to go and look at, and only one banner may say so
    [Fact]
    public void OfflineSupersedesTheApiBeingUnreachable()
        => Assert.Equal("offline", NotchController.NetTrouble(netDown: true, apiDown: true, slow: true));

    [Theory]
    [InlineData("weekly", true)]
    [InlineData("secondary", true)]     // positional: nothing here can verify how long Codex's second bucket is
    [InlineData("5-hour", false)]
    [InlineData("primary", false)]
    public void LongWindowsGetTheCalendar(string window, bool expected)
        => Assert.Equal(expected, NotchController.LongWindow(window));
}
