using Halo.Shell;

namespace Halo.Tests;

public sealed class NotchControllerTests
{
    [Fact]
    public void Decide_NoActiveWidgets_HidesAndReturnsEarly()
    {
        var decision = NotchVisibility.Decide(
            fullscreen: false,
            hasActiveWidgets: false,
            hiddenForFullscreen: false,
            hiddenForNoActiveWidgets: false);

        Assert.Equal(NotchVisibilityAction.Hide, decision.Action);
        Assert.True(decision.ReturnEarly);
        Assert.False(decision.HiddenForFullscreen);
        Assert.True(decision.HiddenForNoActiveWidgets);
    }

    [Fact]
    public void Decide_ActiveWidgetAfterNoWidgets_ShowsAndRendersImmediately()
    {
        var decision = NotchVisibility.Decide(
            fullscreen: false,
            hasActiveWidgets: true,
            hiddenForFullscreen: false,
            hiddenForNoActiveWidgets: true);

        Assert.Equal(NotchVisibilityAction.ShowAndRender, decision.Action);
        Assert.False(decision.ReturnEarly);
        Assert.False(decision.HiddenForFullscreen);
        Assert.False(decision.HiddenForNoActiveWidgets);
    }

    [Fact]
    public void Decide_FullscreenExitWithoutActiveWidgets_StaysHidden()
    {
        var decision = NotchVisibility.Decide(
            fullscreen: false,
            hasActiveWidgets: false,
            hiddenForFullscreen: true,
            hiddenForNoActiveWidgets: false);

        Assert.Equal(NotchVisibilityAction.None, decision.Action);
        Assert.True(decision.ReturnEarly);
        Assert.False(decision.HiddenForFullscreen);
        Assert.True(decision.HiddenForNoActiveWidgets);
    }

    [Fact]
    public void Decide_FullscreenExitWithActiveWidget_ShowsAndRenders()
    {
        var decision = NotchVisibility.Decide(
            fullscreen: false,
            hasActiveWidgets: true,
            hiddenForFullscreen: true,
            hiddenForNoActiveWidgets: false);

        Assert.Equal(NotchVisibilityAction.ShowAndRender, decision.Action);
        Assert.False(decision.ReturnEarly);
        Assert.False(decision.HiddenForFullscreen);
        Assert.False(decision.HiddenForNoActiveWidgets);
    }

    [Fact]
    public void Decide_FullscreenUsesSeparateHiddenFlag()
    {
        var decision = NotchVisibility.Decide(
            fullscreen: true,
            hasActiveWidgets: false,
            hiddenForFullscreen: false,
            hiddenForNoActiveWidgets: false);

        Assert.Equal(NotchVisibilityAction.Hide, decision.Action);
        Assert.True(decision.ReturnEarly);
        Assert.True(decision.HiddenForFullscreen);
        Assert.False(decision.HiddenForNoActiveWidgets);
    }
}
