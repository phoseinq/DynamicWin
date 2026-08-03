using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// A dash inside Persian text drew welded to the next word: it is a bidi neutral, and resolving the neutral
// run swallows the whitespace at the direction change. Fx.PinRtlDashes brackets it with RLM, which was the
// only one of four candidates that actually restored both spaces when rendered. RLM is invisible, so these
// assertions are the only readable record of what the function produces.
public class PinRtlDashesTests
{
    private const string MiBini = "\u0645\u06CC\u200C\u0628\u06CC\u0646\u06CC";
    private const string Tirgi = "\u062A\u06CC\u0631\u06AF\u06CC";
    private const string Em = "\u2014";
    private const string En = "\u2013";
    private const string Rlm = "\u200F";

    [Fact]
    public void An_em_dash_in_persian_text_is_pinned_from_both_sides()
        => Assert.Equal(MiBini + " " + Rlm + Em + Rlm + " " + Tirgi,
                        Fx.PinRtlDashes(MiBini + " " + Em + " " + Tirgi));

    [Fact]
    public void An_en_dash_is_pinned_the_same_way()
        => Assert.Equal(MiBini + " " + Rlm + En + Rlm + " " + Tirgi,
                        Fx.PinRtlDashes(MiBini + " " + En + " " + Tirgi));

    // latin text lays the same dash out correctly on its own, so it must come back untouched - the marks
    // would be dead weight in every english title the pill draws
    [Fact]
    public void Latin_text_is_left_alone()
        => Assert.Equal("deploy - then verify", Fx.PinRtlDashes("deploy - then verify"));

    [Fact]
    public void Latin_text_with_a_real_em_dash_is_also_left_alone()
        => Assert.Equal("deploy " + Em + " then verify", Fx.PinRtlDashes("deploy " + Em + " then verify"));

    // the common case: no dash at all, and the string should not even be copied
    [Fact]
    public void Persian_without_a_dash_comes_back_as_it_went_in()
        => Assert.Equal(MiBini + " " + Tirgi, Fx.PinRtlDashes(MiBini + " " + Tirgi));

    // running twice must not stack marks - the draw path is per-frame
    [Fact]
    public void Pinning_is_idempotent()
    {
        string once = Fx.PinRtlDashes(MiBini + " " + Em + " " + Tirgi);
        Assert.Equal(once, Fx.PinRtlDashes(once));
    }

    [Fact]
    public void A_dash_at_either_end_still_gets_both_marks()
        => Assert.Equal(Rlm + Em + Rlm + Tirgi, Fx.PinRtlDashes(Em + Tirgi));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_nothing_out(string? s)
        => Assert.Equal("", Fx.PinRtlDashes(s));
}
