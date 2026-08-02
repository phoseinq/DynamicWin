using Halo.Shell;

namespace Halo.Tests;

// The greeting arms instead of playing at process start: at boot the logon task is up before explorer
// has a taskbar, so an immediate hand plays to a screen nobody is watching and reads as "hello never
// appears". These pin the arming contract - wait for a watchable screen, settle, and never wait forever.
public class GreetingArmTests
{
    [Fact]
    public void Not_armed_while_the_screen_is_not_watchable()
    {
        float held = 0f, waited = 0f; bool armed = false;
        for (int i = 0; i < 100; i++)
            (held, waited, armed) = GreetingArm.Step(false, held, waited, 0.1f);
        Assert.False(armed);
        Assert.Equal(0f, held);
    }

    [Fact]
    public void Arms_after_the_settle_once_watchable()
    {
        float held = 0f, waited = 0f; bool armed = false;
        int steps = 0;
        while (!armed && steps++ < 1000)
            (held, waited, armed) = GreetingArm.Step(true, held, waited, 0.1f);
        Assert.True(armed);
        Assert.True(held >= GreetingArm.SettleSeconds);
        Assert.True(waited < GreetingArm.GiveUpSeconds); // settle path, not the give-up path
    }

    // explorer restarting mid-hold must restart the settle, not carry credit across the gap
    [Fact]
    public void Losing_the_screen_resets_the_hold()
    {
        var (held, waited, _) = GreetingArm.Step(true, 0f, 0f, 1f);
        Assert.True(held > 0f);
        (held, waited, _) = GreetingArm.Step(false, held, waited, 0.1f);
        Assert.Equal(0f, held);
    }

    // kiosk / replaced shell: the hand must not gate the strip and the pin forever
    [Fact]
    public void Gives_up_waiting_and_arms_anyway()
    {
        float held = 0f, waited = 0f; bool armed = false;
        int steps = 0;
        while (!armed && steps++ < 10000)
            (held, waited, armed) = GreetingArm.Step(false, held, waited, 0.5f);
        Assert.True(armed);
        Assert.True(waited >= GreetingArm.GiveUpSeconds);
    }
}
