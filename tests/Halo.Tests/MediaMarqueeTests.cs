using Halo.Widgets;

namespace Halo.Tests;

// A title too long for an open panel scrolls (Marquee, shared by the media and vlc panels). The motion is
// a rate in px/second, not a step per frame, because the pill drops to a lower fps tier when little is
// happening — a per-frame step would make the scroll speed depend on how busy the machine is, which is the
// same bug the seek bar had.
public class MediaMarqueeTests
{
    [Fact]
    public void It_holds_still_before_the_first_pass_so_the_start_is_readable()
    {
        var (offset, hold) = Marquee.Step(0f, 0f, 0.1f, 300f);
        Assert.Equal(0f, offset);
        Assert.Equal(0.1f, hold, precision: 4);
    }

    [Fact]
    public void It_scrolls_once_the_hold_has_elapsed()
    {
        var (offset, _) = Marquee.Step(0f, Marquee.Hold, 0.5f, 300f);
        Assert.Equal(Marquee.Speed * 0.5f, offset, precision: 3);
    }

    // the point of a rate: the same elapsed time travels the same distance whatever the frame rate
    [Fact]
    public void Frame_rate_does_not_change_how_far_it_travels()
    {
        float slow = 0f, fast = 0f, hold = Marquee.Hold;
        float h1 = hold, h2 = hold;
        for (int i = 0; i < 6; i++) (slow, h1) = Marquee.Step(slow, h1, 1f / 6f, 10_000f);
        for (int i = 0; i < 60; i++) (fast, h2) = Marquee.Step(fast, h2, 1f / 60f, 10_000f);
        Assert.Equal(slow, fast, precision: 3);
        Assert.Equal(Marquee.Speed, slow, precision: 3);   // one second, one speed's worth
    }

    [Fact]
    public void It_wraps_by_exactly_one_span_and_holds_again()
    {
        float span = 200f;
        var (offset, hold) = Marquee.Step(span - 1f, Marquee.Hold, 1f, span);
        Assert.True(offset >= 0f && offset < span, $"offset {offset} left the span");
        Assert.Equal(Marquee.Speed - 1f, offset, precision: 3);
        Assert.Equal(0f, hold);   // pause again at the top of the next pass
    }

    [Fact]
    public void A_zero_span_cannot_divide_by_itself_into_a_runaway_offset()
        => Assert.Equal((0f, 0f), Marquee.Step(500f, 9f, 0.5f, 0f));

    // the unattended (playing, no hover) marquee rests longer between laps than the hovered one: a hold
    // that would already be over under the mouse is still parked when the pass is self-driven
    [Fact]
    public void The_self_driven_pass_rests_longer_than_a_hovered_one()
    {
        var (offset, _) = Marquee.Step(0f, Marquee.Hold + 0.1f, 0.2f, 300f,
            Marquee.Rest);
        Assert.Equal(0f, offset);   // hovered would be moving by now; the rest keeps it parked
        (offset, _) = Marquee.Step(0f, Marquee.Rest, 0.2f, 300f, Marquee.Rest);
        Assert.True(offset > 0f);
    }
}
