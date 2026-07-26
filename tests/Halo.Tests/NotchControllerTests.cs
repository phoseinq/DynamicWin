using Halo.Shell;

namespace Halo.Tests;

public sealed class NotchControllerTests
{
    [Fact]
    public void Decide_Visible_StaysVisibleWithoutRerender()
    {
        var decision = NotchVisibility.Decide(fullscreen: false, hiddenForFullscreen: false);

        Assert.Equal(NotchVisibilityAction.None, decision.Action);
        Assert.False(decision.ReturnEarly);
        Assert.False(decision.HiddenForFullscreen);
    }

    [Fact]
    public void Decide_Fullscreen_HidesAndReturnsEarly()
    {
        var decision = NotchVisibility.Decide(fullscreen: true, hiddenForFullscreen: false);

        Assert.Equal(NotchVisibilityAction.Hide, decision.Action);
        Assert.True(decision.ReturnEarly);
        Assert.True(decision.HiddenForFullscreen);
    }

    [Fact]
    public void Decide_StillFullscreen_DoesNotHideTwice()
    {
        var decision = NotchVisibility.Decide(fullscreen: true, hiddenForFullscreen: true);

        Assert.Equal(NotchVisibilityAction.None, decision.Action);
        Assert.True(decision.ReturnEarly);
        Assert.True(decision.HiddenForFullscreen);
    }

    [Fact]
    public void Decide_FullscreenExit_ShowsAndRenders()
    {
        var decision = NotchVisibility.Decide(fullscreen: false, hiddenForFullscreen: true);

        Assert.Equal(NotchVisibilityAction.ShowAndRender, decision.Action);
        Assert.False(decision.ReturnEarly);
        Assert.False(decision.HiddenForFullscreen);
    }
}
