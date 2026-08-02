using Halo.Shell;
using Xunit;

namespace Halo.Tests;

// The expand/collapse morph used to run at whatever tier the once-a-second CPU sampler had picked, which
// while the panel is open is a deliberate 60 - so the animation never saw 120, and a tier change landing
// inside the ~300ms morph switched cadence mid-movement.
public class CadenceTests
{
    [Fact]
    public void A_morph_always_gets_the_ceiling_even_on_the_watching_tier()
        => Assert.Equal(NotchController.MaxFps, NotchController.CadenceFps(true, 60));

    [Fact]
    public void A_morph_outruns_the_slammed_tier_too()
        => Assert.Equal(NotchController.MaxFps, NotchController.CadenceFps(true, 30));

    // The morph is ~300ms, so running it flat out costs a third of a second of full rate. The settled
    // panel is the thing that had to stay at 60, and it still does.
    [Fact]
    public void The_ceiling_is_above_the_old_120_limit()
        => Assert.True(NotchController.MaxFps > 120);

    [Fact]
    public void Once_settled_the_measured_tier_stands()
    {
        Assert.Equal(60, NotchController.CadenceFps(false, 60));
        Assert.Equal(30, NotchController.CadenceFps(false, 30));
        Assert.Equal(120, NotchController.CadenceFps(false, 120));
    }

    [Theory]
    [InlineData(280, 3.571)]
    [InlineData(240, 4.167)]
    [InlineData(144, 6.944)]
    [InlineData(120, 8.333)]
    [InlineData(60, 16.667)]
    [InlineData(30, 33.333)]
    public void Each_rate_maps_to_its_exact_period(int fps, double ms)
        => Assert.Equal(ms, NotchController.IntervalMs(fps), 3);

    // The reason the period stopped being rounded to whole milliseconds: 240 and 280 both landed on 4ms,
    // so the two choices were the same tick and picking the higher one did nothing at all.
    [Fact]
    public void Two_neighbouring_choices_do_not_collapse_onto_one_tick()
        => Assert.True(NotchController.IntervalMs(280) < NotchController.IntervalMs(240),
            "280 must ask for a shorter period than 240");

    // Picked above MaxFps, the setting has to RAISE what a morph reaches for - capping it away would make
    // the row a control that cannot be honoured.
    [Fact]
    public void A_rate_above_the_built_in_ceiling_is_still_reached_for()
    {
        Assert.Equal(280, NotchController.Reach(280));
        Assert.Equal(280, NotchController.CadenceFps(true, 60, 280));
    }

    [Fact]
    public void With_no_setting_a_morph_reaches_for_the_built_in_ceiling()
        => Assert.Equal(NotchController.MaxFps, NotchController.Reach(0));

    // A ceiling the user picks has to be honoured even when it is above what Halo would choose on its
    // own, and a shorter interval must never come out of a lower number.
    [Fact]
    public void A_higher_tier_never_asks_for_a_longer_interval()
    {
        int[] tiers = [30, 60, 120, 144, 240];
        for (int i = 1; i < tiers.Length; i++)
            Assert.True(NotchController.IntervalMs(tiers[i]) <= NotchController.IntervalMs(tiers[i - 1]),
                $"{tiers[i]}fps asked for a longer interval than {tiers[i - 1]}fps");
    }

    // The ceiling is the user's judgement about their hardware, which a CPU sample cannot make. It is
    // applied last, so it beats the morph's 120 too - otherwise the one moment a weak machine struggles
    // most would be the one moment the setting did not apply.
    [Fact]
    public void The_ceiling_holds_a_morph_down()
        => Assert.Equal(60, NotchController.Capped(NotchController.CadenceFps(true, 60), 60));

    // Ceiling, not target: capping at 60 must not stop a slammed machine dropping to 30.
    [Fact]
    public void A_tier_below_the_ceiling_is_left_alone()
        => Assert.Equal(30, NotchController.Capped(30, 60));

    [Fact]
    public void Auto_is_no_ceiling_at_all()
    {
        Assert.Equal(120, NotchController.Capped(120, 0));
        Assert.Equal(NotchController.MaxFps, NotchController.Capped(NotchController.CadenceFps(true, 30), 0));
    }

    // Auto means MaxFps now, so a user who wants the old behaviour has to be able to ask for it.
    [Fact]
    public void A_user_can_pin_the_rate_back_down_to_the_old_limit()
        => Assert.Equal(120, NotchController.Capped(NotchController.CadenceFps(true, 60), 120));

    [Fact]
    public void A_ceiling_above_the_tier_changes_nothing()
        => Assert.Equal(60, NotchController.Capped(60, 120));

    // The dark flash people saw while the pill grew: the collapsed preview was gone by t=0.35 and the
    // expanded content did not start until t=0.45, so a tenth of the morph drew nothing at all. Swept
    // rather than spot-checked, because the hole was ten percent wide and a handful of samples walks
    // straight over it.
    [Fact]
    public void Something_is_drawn_at_every_point_of_the_morph()
    {
        for (int i = 0; i <= 1000; i++)
        {
            float t = i / 1000f;
            Assert.True(NotchController.MorphHasContent(t),
                $"nothing drawn at t={t:F3}: content={NotchController.ContentFade(t):F3} "
                + $"mini={NotchController.MiniFade(t):F3}");
        }
    }

    // The two have to overlap, not merely meet: touching at a point is one frame of near-nothing at any
    // frame rate slow enough to land on it.
    [Fact]
    public void The_preview_is_still_up_when_the_content_starts()
        => Assert.True(NotchController.ContentIn < NotchController.MiniOut,
            "content must begin before the preview has finished melting");

    [Fact]
    public void The_ends_are_still_clean()
    {
        Assert.Equal(0f, NotchController.ContentFade(0f));
        Assert.Equal(1f, NotchController.ContentFade(1f));
        Assert.Equal(1f, NotchController.MiniFade(0f));
        Assert.Equal(0f, NotchController.MiniFade(1f));
    }
}
