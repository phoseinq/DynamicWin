using Halo.Shell;
using Halo.Widgets;

namespace Halo.Tests;

public sealed class AgentNoticeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");

    [Fact]
    public void WaitingCodex_TemporarilyBecomesPrimaryThenRestores()
    {
        var state = new AgentNoticeCoordinator(primary: 0);

        state.Observe(widgetIndex: 2, new AgentNotice("waiting_input", null, "approve?"), Now);

        Assert.Equal(2, state.Primary);
        state.Tick(Now.AddSeconds(7));
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

        state.Observe(widgetIndex: 1, new AgentNotice("working", null, null), Now);

        Assert.False(state.IsOpen(Now));
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

        state.Observe(widgetIndex: 1, new AgentNotice("waiting_input", null, "Claude?"), Now);
        state.Observe(widgetIndex: 2, new AgentNotice("waiting_input", null, "Codex?"), Now, desktopBacked: true);

        Assert.Equal(2, state.Primary);
    }
}
