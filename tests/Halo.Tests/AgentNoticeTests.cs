using Halo.Shell;
using Halo.Widgets;

namespace Halo.Tests;

public sealed class AgentNoticeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");

    [Fact]
    public void WaitingInput_DoesNotStealThePill()
    {
        var state = new AgentNoticeCoordinator(primary: 0);

        // waiting_input used to pop the pill open; the user asked for it to stay put (compact-done
        // is still allowed to take over). This test pins that decision so it can't regress silently.
        state.Observe(widgetIndex: 2, new AgentNotice("waiting_input", null, "approve?"), Now);

        Assert.False(state.IsOpen(Now));
        Assert.Equal(0, state.Primary);
    }

    [Fact]
    public void CompactCompletion_HoldsForFourSeconds()
    {
        var state = new AgentNoticeCoordinator(primary: 0);
        state.Observe(widgetIndex: 1, new AgentNotice("compacting", null, null), Now);

        state.Observe(widgetIndex: 1, new AgentNotice("working", Now, null), Now);

        Assert.Equal(1, state.Primary);
        state.Tick(Now.AddSeconds(4));
        Assert.Equal(1, state.Primary);
        state.Tick(Now.AddSeconds(5));
        Assert.Equal(0, state.Primary);
    }

    [Fact]
    public void CancelledCompact_DoesNotAnnounceCompletion()
    {
        var state = new AgentNoticeCoordinator(primary: 0);
        state.Observe(widgetIndex: 1, new AgentNotice("compacting", null, null), Now);

        // esc-cancelled: state leaves "compacting" but compactedAt never gets written
        state.Observe(widgetIndex: 1, new AgentNotice("working", null, null), Now);

        Assert.False(state.IsOpen(Now)); // no "compacted :)" notice window
    }

    [Fact]
    public void StaleCompactedAt_DoesNotAnnounceAtStartup()
    {
        var state = new AgentNoticeCoordinator(primary: 0);

        state.Observe(widgetIndex: 1, new AgentNotice("idle", Now.AddMinutes(-10), null), Now);

        Assert.False(state.IsOpen(Now));
        Assert.Equal(0, state.Primary);
    }

    [Fact]
    public void SimultaneousNotices_PreferDesktopCodexWhenCurrentWidgetIsNotAnAgent()
    {
        var state = new AgentNoticeCoordinator(primary: 0);

        // two compact-done notices land in the same tick and neither widget is the current primary,
        // so the desktop-backed one wins. (Written with compact-done because waiting_input no longer
        // opens a notice window at all — see WaitingInput_DoesNotStealThePill.)
        state.Observe(widgetIndex: 1, new AgentNotice("compacting", null, null), Now);
        state.Observe(widgetIndex: 2, new AgentNotice("compacting", null, null), Now, desktopBacked: true);
        state.Observe(widgetIndex: 1, new AgentNotice("working", Now, null), Now);
        state.Observe(widgetIndex: 2, new AgentNotice("working", Now, null), Now, desktopBacked: true);

        Assert.Equal(2, state.Primary);
    }
}
