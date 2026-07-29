using System;
using System.Drawing;
using Halo.Agents;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The ring around the icon is the pill's only always-visible signal, and it carries more than the four
// states now. What has to hold is that it stays readable: warm means the session is under pressure, red
// still means broken, the small hours are quieter, and nothing here snaps.
public class MoodRingTests
{
    private static readonly Color Working = Color.FromArgb(62, 207, 92);   // the widgets' Green
    private static readonly Color Error = Color.FromArgb(229, 72, 77);     // …and their Red
    private static readonly MoodContext Daytime = new(Hour: 14);

    [Fact]
    public void AnUnremarkableMomentLeavesTheStateColourAlone()
    {
        var c = Fx.MoodRing(Working, Daytime);
        // hsv there and back can land a unit out; what matters is that nothing moved
        Assert.InRange(c.R, Working.R - 2, Working.R + 2);
        Assert.InRange(c.G, Working.G - 2, Working.G + 2);
        Assert.InRange(c.B, Working.B - 2, Working.B + 2);
    }

    [Fact]
    public void ATighteningSessionWarmsIt()
    {
        var calm = Fx.MoodRing(Working, Daytime);
        var tight = Fx.MoodRing(Working, new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Assert.True(tight.R > calm.R + 20, $"{tight} is no warmer than {calm}");
        Assert.True(tight.G < calm.G, $"{tight} did not give up any green");
    }

    [Fact]
    public void ASpentUsageWindowWarmsItToo()
    {
        var calm = Fx.MoodRing(Working, Daytime);
        var thin = Fx.MoodRing(Working, new MoodContext(UsageFrac: 0.97f, Hour: 14));
        Assert.True(thin.R > calm.R + 20, $"{thin} is no warmer than {calm}");
    }

    // the point of a ramp rather than a threshold: halfway up the band is halfway warm, so the ring drifts
    // as the session fills instead of flipping colour at one figure
    [Fact]
    public void ItRampsRatherThanSnapping()
    {
        var calm = Fx.MoodRing(Working, new MoodContext(ContextFrac: 0.40f, Hour: 14));
        var mid = Fx.MoodRing(Working, new MoodContext(ContextFrac: 0.75f, Hour: 14));
        var full = Fx.MoodRing(Working, new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Assert.InRange(mid.R, calm.R + 1, full.R - 1);
    }

    [Fact]
    public void ADraggingTurnWarmsALittleButLessThanPressureDoes()
    {
        var calm = Fx.MoodRing(Working, Daytime);
        var dragging = Fx.MoodRing(Working, new MoodContext(Running: TimeSpan.FromMinutes(15), Hour: 14));
        var tight = Fx.MoodRing(Working, new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Assert.True(dragging.R > calm.R, "a long turn should read warmer than a fresh one");
        Assert.True(dragging.R < tight.R, "a slow turn is not the same news as a full context");
    }

    // red on this ring means a failure. A session merely under pressure must never be able to arrive there,
    // or the one colour that means "something is broken" stops meaning it.
    [Fact]
    public void PressureNeverArrivesAtTheColourThatMeansBroken()
    {
        foreach (var f in new[] { 0.80f, 0.90f, 0.95f, 1.00f })
        {
            var c = Fx.MoodRing(Working, new MoodContext(
                ContextFrac: f, UsageFrac: f, Running: TimeSpan.FromHours(1), Hour: 14));
            Assert.True(c.G > Error.G + 25, $"at {f} the ring is {c}, too close to the error red {Error}");
        }
    }

    [Fact]
    public void TheSmallHoursAreQuieterNotADifferentColour()
    {
        var day = Fx.MoodRing(Working, Daytime);
        var night = Fx.MoodRing(Working, new MoodContext(Hour: 2));
        Assert.True(night.G < day.G, $"{night} is not quieter than {day}");
        Assert.True(night.G > night.R && night.G > night.B, $"{night} is no longer the working colour");
    }

    // an idle white ring has no hue to move, so this is the case where a bad lerp shows up as grey
    [Fact]
    public void TheIdleRingWarmsWithoutGoingGrey()
    {
        var white = Color.FromArgb(238, 255, 255, 255);
        var tight = Fx.MoodRing(white, new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Assert.Equal(white.A, tight.A);
        Assert.True(tight.R >= tight.G && tight.G > tight.B, $"{tight} is not a warm white");
        Assert.True(tight.R > 200, $"{tight} lost its brightness");
    }
}
