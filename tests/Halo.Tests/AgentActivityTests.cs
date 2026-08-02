using System;
using Halo.Widgets;

namespace Halo.Tests;

// The strip orders a group's sessions by AgentActivity.Rank and the controller picks the busiest working
// session for the pill. State always outranks elapsed time; elapsed time only breaks ties inside a state.
public class AgentActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void States_rank_working_over_compacting_over_asking_over_waiting_over_idle()
    {
        long working = AgentActivity.Rank("working", Now, Now);
        long compacting = AgentActivity.Rank("compacting", Now, Now);
        long asking = AgentActivity.Rank("waiting_input", Now, Now);
        long waiting = AgentActivity.Rank("waiting", Now, Now);
        long idle = AgentActivity.Rank("idle", Now, Now);
        Assert.True(working > compacting);
        Assert.True(compacting > asking);
        Assert.True(asking > waiting);
        Assert.True(waiting > idle);
        Assert.Equal(0, idle);
    }

    [Fact]
    public void Null_and_empty_state_rank_zero()
    {
        Assert.Equal(0, AgentActivity.Rank(null, Now.AddHours(-1), Now));
        Assert.Equal(0, AgentActivity.Rank("", Now.AddHours(-1), Now));
    }

    // between two working sessions the one deeper into its turn wins
    [Fact]
    public void Longer_running_turn_outranks_a_fresh_one_in_the_same_state()
    {
        long old_ = AgentActivity.Rank("working", Now.AddMinutes(-10), Now);
        long fresh = AgentActivity.Rank("working", Now.AddSeconds(-5), Now);
        Assert.True(old_ > fresh);
    }

    // no elapsed time can promote a session past a busier state
    [Fact]
    public void Elapsed_time_never_crosses_a_state_boundary()
    {
        long freshWorking = AgentActivity.Rank("working", Now, Now);
        long ancientCompacting = AgentActivity.Rank("compacting", Now.AddDays(-30), Now);
        Assert.True(freshWorking > ancientCompacting);
    }

    [Fact]
    public void Missing_start_still_ranks_by_state()
    {
        long noStart = AgentActivity.Rank("working", null, Now);
        long idle = AgentActivity.Rank("idle", Now.AddHours(-1), Now);
        Assert.True(noStart > idle);
        long withStart = AgentActivity.Rank("working", Now.AddSeconds(-1), Now);
        Assert.True(withStart > noStart);
    }

    // a clock-skewed future start must not go negative and sink below a no-start session
    [Fact]
    public void Future_start_clamps_to_the_state_floor()
    {
        long skewed = AgentActivity.Rank("working", Now.AddMinutes(5), Now);
        Assert.Equal(AgentActivity.Rank("working", null, Now), skewed);
    }

    [Fact]
    public void Unknown_state_beats_idle_but_nothing_else()
    {
        long odd = AgentActivity.Rank("error", Now, Now);
        Assert.True(odd > 0);
        Assert.True(odd < AgentActivity.Rank("waiting", Now, Now));
    }
}
