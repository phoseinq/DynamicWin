using Halo.Shell;
using Xunit;

namespace Halo.Tests;

// The thresholds became settings, and the thing worth pinning is not that a number is read from a file -
// it is what the ladder does around the number the user picked. "Warn me later" must not silently also
// mean "and then stop escalating", and a battery low set at or under the critical rung must not fire two
// warnings at the same instant.
public class AlertThresholdTests
{
    private static readonly int[] Cpu = [50, 70, 85, 95];
    private static readonly int[] Ram = [70, 85, 95];

    [Fact]
    public void Chosen_value_becomes_the_first_rung()
    {
        Assert.Equal([60, 70, 85, 95], NotchController.Tiers(Cpu, 60));
    }

    [Fact]
    public void Rungs_below_the_chosen_value_are_dropped()
    {
        Assert.Equal([80, 85, 95], NotchController.Tiers(Cpu, 80));
        Assert.Equal([90, 95], NotchController.Tiers(Ram, 90));
    }

    [Fact]
    public void Higher_rungs_survive_so_escalation_still_happens()
    {
        var tiers = NotchController.Tiers(Cpu, 55);
        Assert.True(tiers.Length > 1, "a later first warning must still escalate above itself");
        Assert.Equal(55, tiers[0]);
    }

    [Fact]
    public void A_value_above_every_fixed_rung_leaves_exactly_one()
    {
        Assert.Equal([99], NotchController.Tiers(Cpu, 99));
    }

    [Fact]
    public void The_chosen_value_is_never_duplicated_when_it_matches_a_fixed_rung()
    {
        Assert.Equal([70, 85, 95], NotchController.Tiers(Cpu, 70));
    }

    // Descending: a battery gets worse as the number falls, so -1 is "fine", 0 is low, 1 is critical.
    [Fact]
    public void Battery_ladder_ratchets_downward()
    {
        int[] ladder = [30, 10];
        Assert.Equal(-1, NotchController.BatteryTier(45, ladder));
        Assert.Equal(0, NotchController.BatteryTier(30, ladder));
        Assert.Equal(0, NotchController.BatteryTier(11, ladder));
        Assert.Equal(1, NotchController.BatteryTier(10, ladder));
    }

    [Fact]
    public void Battery_low_at_or_below_critical_gives_one_warning_not_two()
    {
        int[] collapsed = [10];
        Assert.Equal(-1, NotchController.BatteryTier(11, collapsed));
        Assert.Equal(0, NotchController.BatteryTier(10, collapsed));
        Assert.Equal(0, NotchController.BatteryTier(3, collapsed));
    }
}
