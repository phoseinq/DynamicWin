using Halo.Shell;

namespace Halo.Tests;

public sealed class NotchVisibilityTests
{
    [Theory]
    [InlineData(false, false, 0, false, false)]
    [InlineData(true, false, 1, true, true)]
    [InlineData(true, true, 0, true, true)]
    [InlineData(false, true, 2, false, false)]
    public void Decide_TransitionsFullscreenVisibility(
        bool fullscreen,
        bool hiddenForFullscreen,
        int expectedAction,
        bool expectedReturnEarly,
        bool expectedHidden)
    {
        var decision = NotchVisibility.Decide(fullscreen, hiddenForFullscreen);

        Assert.Equal((NotchVisibilityAction)expectedAction, decision.Action);
        Assert.Equal(expectedReturnEarly, decision.ReturnEarly);
        Assert.Equal(expectedHidden, decision.HiddenForFullscreen);
    }
}
