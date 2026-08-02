using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// Bars were drawn straight from their source, and every source arrives in jumps. The rules that matter are
// not "it animates" - they are that it converges, and that it never shows more than is true.
public class EasedBarTests
{
    [Fact]
    public void The_first_value_is_taken_as_it_is()
    {
        var bar = new EasedBar();
        Assert.Equal(0.9f, bar.Step(0.9f, 0.008f));
    }

    [Fact]
    public void A_jump_is_crossed_over_several_frames_not_one()
    {
        var bar = new EasedBar();
        bar.Step(0f, 0.008f);
        float after = bar.Step(1f, 0.008f);

        Assert.True(after > 0f, "it has to move");
        Assert.True(after < 0.05f, $"one 8ms frame should cover about 1%, got {after:P1}");
    }

    // The whole point. A fill that reads ahead of the download is a number nobody measured.
    [Fact]
    public void The_shown_value_never_passes_the_real_one()
    {
        var bar = new EasedBar();
        bar.Step(0f, 0.008f);
        for (int i = 0; i < 500; i++)
            Assert.True(bar.Step(0.5f, 0.008f) <= 0.5f, "overshot the truth");
    }

    [Fact]
    public void It_lands_exactly_on_the_target_rather_than_near_it()
    {
        var bar = new EasedBar();
        bar.Step(0f, 0.008f);
        for (int i = 0; i < 500; i++) bar.Step(1f, 0.008f);

        Assert.Equal(1f, bar.Shown);
    }

    [Fact]
    public void It_eases_downwards_too()
    {
        var bar = new EasedBar();
        bar.Step(1f, 0.008f);
        float after = bar.Step(0f, 0.008f);

        Assert.True(after < 1f && after > 0.9f, $"should have edged down, got {after}");
    }

    // The pill runs anywhere from 30 to 120fps and the ceiling is now a setting, so the same download must
    // not crawl on a machine that merely chose 30.
    [Fact]
    public void The_rate_is_per_second_not_per_frame()
    {
        var fast = new EasedBar();
        var slow = new EasedBar();
        fast.Step(0f, 0.008f);
        slow.Step(0f, 0.032f);

        // the same 32ms of wall clock, spent as four 120fps frames or one 30fps frame
        for (int i = 0; i < 4; i++) fast.Step(1f, 0.008f);
        slow.Step(1f, 0.032f);

        Assert.Equal(slow.Shown, fast.Shown, 3);
    }

    [Fact]
    public void A_reset_makes_the_next_value_a_fresh_start()
    {
        var bar = new EasedBar();
        bar.Step(1f, 0.008f);
        bar.Reset();

        Assert.Equal(0.2f, bar.Step(0.2f, 0.008f));
    }

    [Fact]
    public void A_source_outside_zero_to_one_cannot_drag_the_fill_out_of_range()
    {
        var bar = new EasedBar();
        Assert.Equal(1f, bar.Step(4f, 0.008f));

        var under = new EasedBar();
        Assert.Equal(0f, under.Step(-2f, 0.008f));
    }

    // A stalled frame must advance one step, not leap - the same reason NotchController clamps its own dt.
    [Fact]
    public void A_long_stall_does_not_become_a_jump()
    {
        var bar = new EasedBar();
        bar.Step(0f, 0.008f);

        Assert.True(bar.Step(1f, 5f) < 0.1f, "a five second stall should still move about one step");
    }
}
