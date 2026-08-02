using Halo.Shell;
using Xunit;

namespace Halo.Tests;

// The settings row used to be able to do nothing but read the user's own choice back to them: picking 280
// asks the timer for a 3.571ms period, and whether the platform delivers it is a different question. So
// the pill measures a morph and the panel shows that number instead.
public class MorphRateTests
{
    private static MorphRate Morph(int frames, double dt)
    {
        var rate = new MorphRate();
        for (int i = 0; i < frames; i++) Assert.False(rate.Step(true, dt));
        return rate;
    }

    [Fact]
    public void Nothing_is_claimed_before_a_morph_has_been_seen()
        => Assert.Equal(0, new MorphRate().Measured);

    [Fact]
    public void A_settled_pill_reports_nothing_at_all()
    {
        var rate = new MorphRate();
        for (int i = 0; i < 500; i++) Assert.False(rate.Step(false, 1 / 60.0));
        Assert.Equal(0, rate.Measured);
    }

    // 36 frames across 150ms is 240fps, and it is only reported once the morph has ENDED - mid-morph the
    // divisor is still moving.
    [Fact]
    public void The_rate_lands_when_the_morph_ends()
    {
        var rate = Morph(36, 1 / 240.0);
        Assert.Equal(0, rate.Measured);
        Assert.True(rate.Step(false, 1 / 240.0));
        Assert.Equal(240, rate.Measured);
    }

    [Fact]
    public void A_timer_that_missed_its_period_is_reported_as_what_it_managed()
    {
        // asked for 280, delivered ~190: the whole reason the row exists
        var rate = Morph(38, 1 / 190.0);
        Assert.True(rate.Step(false, 1 / 190.0));
        Assert.Equal(190, rate.Measured);
    }

    // A three-frame twitch divides by a duration barely longer than one tick, so the answer is mostly the
    // noise in where the samples landed.
    [Fact]
    public void A_movement_too_short_to_mean_anything_is_not_evidence()
    {
        var rate = Morph(3, 1 / 240.0);
        Assert.False(rate.Step(false, 1 / 240.0));
        Assert.Equal(0, rate.Measured);
    }

    [Fact]
    public void Enough_frames_but_no_duration_is_still_not_evidence()
    {
        var rate = Morph(MorphRate.MinFrames, 0.001);
        Assert.False(rate.Step(false, 0.001));
        Assert.Equal(0, rate.Measured);
    }

    // The caller writes a file on a true, so an unchanged number must not come back as one.
    [Fact]
    public void The_same_rate_twice_is_reported_once()
    {
        var rate = Morph(36, 1 / 240.0);
        Assert.True(rate.Step(false, 1 / 240.0));
        for (int i = 0; i < 36; i++) rate.Step(true, 1 / 240.0);
        Assert.False(rate.Step(false, 1 / 240.0));
        Assert.Equal(240, rate.Measured);
    }

    [Fact]
    public void A_discarded_morph_does_not_leak_into_the_next_one()
    {
        var rate = new MorphRate();
        for (int i = 0; i < 3; i++) rate.Step(true, 1 / 240.0);
        rate.Step(false, 1 / 240.0);              // too short, thrown away
        for (int i = 0; i < 36; i++) rate.Step(true, 1 / 240.0);
        Assert.True(rate.Step(false, 1 / 240.0));
        Assert.Equal(240, rate.Measured);          // not 39 frames' worth
    }

    // The file is a contract with a different executable, so its shape is pinned here rather than left to
    // whatever string interpolation happened to produce.
    [Fact]
    public void The_report_is_two_integers_and_nothing_else()
    {
        Assert.Equal("231 280", RateReport.Format(231, 280));
        Assert.Equal("0 0", RateReport.Format(0, 0));
    }
}
