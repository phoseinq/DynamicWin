using System.Text.RegularExpressions;
using Halo.Widgets;

namespace Halo.Tests;

public sealed class VlcHttpTests
{
    [Fact]
    public void SetKey_UncommentsAndSetsValue()
    {
        var outp = VlcHttp.SetKey("[lua]\n#http-password=\n#http-src=\n", "http-password", "abc123");
        Assert.Contains("http-password=abc123", outp);
        Assert.DoesNotContain("#http-password=", outp);
        Assert.Equal("abc123", VlcHttp.ReadKey(outp, "http-password"));
    }

    [Fact]
    public void SetKey_IsIdempotent()
    {
        var a = VlcHttp.SetKey("#extraintf=\n", "extraintf", "http");
        var b = VlcHttp.SetKey(a, "extraintf", "http");
        Assert.Equal(a, b);
        Assert.Single(Regex.Matches(b, "(?m)^extraintf="));
    }

    [Fact]
    public void SetKey_AppendsWhenAbsent()
        => Assert.Equal("http", VlcHttp.ReadKey(VlcHttp.SetKey("[core]\n", "extraintf", "http"), "extraintf"));

    [Fact]
    public void ReadKey_IgnoresCommentedDefault()
        => Assert.Null(VlcHttp.ReadKey("[lua]\n#http-password=\n", "http-password"));

    [Fact]
    public void ParseStatus_ReadsRateAndState()
    {
        var (rate, playing) = VlcHttp.ParseStatus("<root><rate>1.5</rate><state>paused</state></root>");
        Assert.Equal(1.5, rate, 3);
        Assert.False(playing);
    }

    [Theory]
    [InlineData(1.0, 1.25)]
    [InlineData(1.25, 1.5)]
    [InlineData(1.5, 2.0)]
    [InlineData(2.0, 1.0)]   // wrap past the top
    [InlineData(1.3, 1.5)]   // odd rate set inside VLC → next preset up
    public void NextPreset_StepsUpThenWraps(double cur, double expected)
        => Assert.Equal(expected, VlcHttp.NextPreset(cur, new[] { 1.0, 1.25, 1.5, 2.0 }));
}
