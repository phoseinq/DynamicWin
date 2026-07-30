using Halo.Notifications;

namespace Halo.Tests;

// The restart that applies a per-app suppression was landing ~3s after the toast that triggered it, which
// is inside most notification sounds — the chime started at full volume and was cut off partway. These pin
// the two rules that decide when a restart is allowed, because they pull in opposite directions and the
// old code checked them in two different places.
public class BannerGateTests
{
    private const int Quiet = 12_000;
    private const int Cooldown = 60_000;

    [Fact]
    public void Waits_out_the_sound_of_the_toast_that_just_landed()
    {
        // toast at t=0, last restart long ago: the whole quiet gap still has to pass
        Assert.Equal(Quiet, BannerGate.ApplyDelayMs(now: 0, lastRestart: -Cooldown, lastToast: 0));
    }

    // Enable() USED to stamp lastToast at launch, so the startup restart - the one that makes a service
    // started by logon re-read the zeros already sitting in the registry - waited out a full quiet gap
    // before firing. The politeness cost a 12 second hole at every start in which the stale service was
    // still the one deciding, and every toast arriving in it banged at full volume. Nothing pending, no
    // recent restart, no toast on record now means: go.
    [Fact]
    public void Startup_refreshes_the_stale_service_immediately()
        => Assert.Equal(0, BannerGate.ApplyDelayMs(now: 0, lastRestart: -Cooldown, lastToast: -Quiet));

    [Fact]
    public void A_later_toast_pushes_a_pending_restart_back_out()
    {
        // 10s into the gap another toast arrives — the wait restarts rather than finishing 2s later
        Assert.Equal(Quiet, BannerGate.ApplyDelayMs(now: 10_000, lastRestart: -Cooldown, lastToast: 10_000));
    }

    [Fact]
    public void Fires_once_the_notifications_have_gone_quiet()
    {
        Assert.Equal(0, BannerGate.ApplyDelayMs(now: Quiet, lastRestart: -Cooldown, lastToast: 0));
    }

    // ...but not forever. On a machine that toasts every few seconds each arrival pushed the pending
    // restart back by the whole gap, so it could starve and the session ran on with a service whose cache
    // predates every zero in the registry - which is the reported symptom, a notification sound that keeps
    // coming while no banner ever appears. One truncated sound is cheaper than a session of them.
    [Fact]
    public void A_pending_restart_cannot_be_deferred_forever()
    {
        // pending for 25s and toasts still arriving: still politely waiting
        Assert.Equal(Quiet, BannerGate.ApplyDelayMs(now: 25_000, lastRestart: -Cooldown, lastToast: 25_000,
            pendingSince: 0 + 1));
        // pending for 31s: the gap is dropped and it goes
        Assert.Equal(0, BannerGate.ApplyDelayMs(now: 31_000, lastRestart: -Cooldown, lastToast: 31_000,
            pendingSince: 1));
    }

    // the cooldown is NOT dropped by that: it exists to stop restart thrash and outranks the sound
    [Fact]
    public void The_starvation_guard_still_respects_the_cooldown()
        => Assert.Equal(20_000, BannerGate.ApplyDelayMs(now: 40_000, lastRestart: 0, lastToast: 40_000,
            pendingSince: 1));

    [Fact]
    public void Cooldown_still_applies_when_the_gap_has_already_passed()
    {
        // quiet for 20s, but the previous restart was only 20s ago → 40s of cooldown left
        Assert.Equal(40_000, BannerGate.ApplyDelayMs(now: 20_000, lastRestart: 0, lastToast: 0));
    }

    [Fact]
    public void The_longer_of_the_two_rules_wins()
    {
        // cooldown has 5s left, but a toast landed 1s ago → the sound is what we wait for
        Assert.Equal(Quiet - 1_000,
            BannerGate.ApplyDelayMs(now: 100_000, lastRestart: 100_000 - (Cooldown - 5_000), lastToast: 99_000));
    }

    [Fact]
    public void Never_returns_a_negative_delay()
    {
        Assert.Equal(0, BannerGate.ApplyDelayMs(now: 10_000_000, lastRestart: 0, lastToast: 0));
    }
}
