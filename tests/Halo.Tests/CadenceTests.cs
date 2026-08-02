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
}
