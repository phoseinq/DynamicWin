using Halo.Shell;
using Xunit;

namespace Halo.Tests;

// The expand/collapse morph used to run at whatever tier the once-a-second CPU sampler had picked, which
// while the panel is open is a deliberate 60 - so the animation never saw 120, and a tier change landing
// inside the ~300ms morph switched cadence mid-movement.
public class CadenceTests
{
    [Fact]
    public void A_morph_always_gets_120_even_on_the_watching_tier()
        => Assert.Equal(120, NotchController.CadenceFps(true, 60));

    [Fact]
    public void A_morph_outruns_the_slammed_tier_too()
        => Assert.Equal(120, NotchController.CadenceFps(true, 30));

    [Fact]
    public void Once_settled_the_measured_tier_stands()
    {
        Assert.Equal(60, NotchController.CadenceFps(false, 60));
        Assert.Equal(30, NotchController.CadenceFps(false, 30));
        Assert.Equal(120, NotchController.CadenceFps(false, 120));
    }

    [Theory]
    [InlineData(120, 8)]
    [InlineData(60, 16)]
    [InlineData(30, 33)]
    public void Each_tier_maps_to_its_timer_interval(int fps, int ms)
        => Assert.Equal(ms, NotchController.IntervalMs(fps));

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
        Assert.Equal(120, NotchController.Capped(NotchController.CadenceFps(true, 30), 0));
    }

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
