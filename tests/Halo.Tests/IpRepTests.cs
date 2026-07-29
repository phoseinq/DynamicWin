using Halo.ClaudeCode;
using Xunit;

namespace Halo.Tests;

public class IpRepTests
{
    [Fact]
    public void PlainResidentialExitIsClean()
    {
        var (verdict, sev) = IpRep.Classify(false, false, false, false, false, false, false, "low");
        Assert.Equal("residential", verdict);
        Assert.Equal(0, sev);
    }

    [Fact]
    public void DatacenterIsNoticedButNotWarned()
    {
        var (verdict, sev) = IpRep.Classify(false, false, false, false, false, true, false, "low");
        Assert.Equal("datacenter", verdict);
        Assert.Equal(1, sev);
    }

    // the live exit this was built against: a hosting network whose operator ipapi.is labels a high abuser
    [Fact]
    public void HighAbuseOperatorLiftsDatacenterIntoTheWarningBand()
    {
        var (verdict, sev) = IpRep.Classify(false, false, false, false, false, true, false, "high");
        Assert.Equal("datacenter", verdict);
        Assert.Equal(2, sev);
    }

    [Fact]
    public void AbuseNeverDowngradesAFlaggedExit()
    {
        var (_, sev) = IpRep.Classify(true, false, false, false, false, false, false, "low");
        Assert.Equal(3, sev);
    }

    [Theory]
    [InlineData(true, false, false, "flagged: tor")]
    [InlineData(false, true, false, "flagged: abuse")]
    [InlineData(false, false, true, "flagged: bogon")]
    public void TheFlaggedCasesOutrankEverythingElse(bool tor, bool abuser, bool bogon, string expected)
    {
        // every lesser flag is also set, so this pins the precedence and not just the mapping
        var (verdict, sev) = IpRep.Classify(tor, abuser, bogon, true, true, true, true, "low");
        Assert.Equal(expected, verdict);
        Assert.Equal(3, sev);
    }

    [Fact]
    public void VpnOutranksAPlainDatacenter()
    {
        var (verdict, sev) = IpRep.Classify(false, false, false, true, false, true, false, null);
        Assert.Equal("vpn, recognised", verdict);
        Assert.Equal(2, sev);
    }

    [Theory]
    [InlineData("0.0586 (High)", "high")]
    [InlineData("0.0079 (Low)", "low")]
    [InlineData("0.9 (Very High)", "very high")]
    public void TheAbuseLabelIsTheParentheticalLowercased(string raw, string expected)
        => Assert.Equal(expected, IpRep.AbuseLabel(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0586")]      // no parenthetical at all
    [InlineData("0.0586 (")]    // truncated, so there is nothing to read between the brackets
    [InlineData("0.0586 ()")]
    public void AnUnreadableAbuseFieldIsSimplyAbsent(string? raw)
        => Assert.Null(IpRep.AbuseLabel(raw));

    [Fact]
    public void AnUnremarkableExitScoresFull()
        => Assert.Equal(100, IpRep.Score(false, false, false, false, false, false, "low", false, false));

    // the live exit this was built against: hosting network, operator ipapi.is labels a high abuser, and a
    // dns test that found resolvers outside the exit country
    [Fact]
    public void TheMeasuredCaseAddsUp()
    {
        Assert.Equal(72, IpRep.Score(false, false, false, false, false, true, "high", false, false));
        Assert.Equal(52, IpRep.Score(false, false, false, false, false, true, "high", false, true));
    }

    [Fact]
    public void OurOwnTwoMeasurementsCostPointsOnTheirOwn()
    {
        int clean = IpRep.Score(false, false, false, false, false, false, null, false, false);
        Assert.Equal(clean - 12, IpRep.Score(false, false, false, false, false, false, null, true, false));
        Assert.Equal(clean - 20, IpRep.Score(false, false, false, false, false, false, null, false, true));
    }

    [Fact]
    public void AVpnAndAProxyCostTheSame()
        => Assert.Equal(IpRep.Score(false, false, false, true, false, false, null, false, false),
                        IpRep.Score(false, false, false, false, true, false, null, false, false));

    // both set is one recognised-relay fact, not two: a provider flagging vpn AND proxy for one address
    // must not be charged twice
    [Fact]
    public void VpnAndProxyTogetherAreChargedOnce()
        => Assert.Equal(IpRep.Score(false, false, false, true, false, false, null, false, false),
                        IpRep.Score(false, false, false, true, true, false, null, false, false));

    [Fact]
    public void TheWorstCaseFloorsAtZeroRatherThanGoingNegative()
        => Assert.Equal(0, IpRep.Score(true, true, true, true, true, true, "very high", true, true));

    [Theory]
    [InlineData(false, false, 100)]
    [InlineData(true, false, 45)]    // tor alone
    [InlineData(false, true, 55)]    // a listed abuser alone
    public void EachFlaggedFindingIsPricedOnItsOwn(bool tor, bool abuser, int expected)
        => Assert.Equal(expected, IpRep.Score(tor, abuser, false, false, false, false, null, false, false));
}
