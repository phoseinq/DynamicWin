using System;
using System.Drawing;
using Halo.Agents;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The ring around the icon is the pill's only always-visible signal. Its hue is the state (see
// SlotColorTests) and pressure rides on top, under one rule: a hue that says WHICH activity this is may
// not be repurposed, and the two hues that say nothing - amber for thinking, white for idle - may be.
// hueIsFree is that rule. What has to hold here: warm still means pressure, red still means broken, the
// small hours are quieter, and nothing snaps.
public class MoodRingTests
{
    private static readonly Color Thinking = Color.FromArgb(255, 165, 31);  // amber: hue carries no activity
    private static readonly Color Working = Color.FromArgb(62, 207, 92);    // green: hue carries "shell"
    private static readonly Color Error = Color.FromArgb(229, 72, 77);      // the widgets' Red
    private static readonly MoodContext Daytime = new(Hour: 14);

    private static Color Free(MoodContext ctx) => Fx.MoodRing(Thinking, ctx, hueIsFree: true);
    private static Color Held(MoodContext ctx) => Fx.MoodRing(Working, ctx, hueIsFree: false);

    private static int Dist(Color a, Color b)
        => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    [Fact]
    public void AnUnremarkableMomentLeavesTheStateColourAlone()
    {
        var c = Free(Daytime);
        // hsv there and back can land a unit out; what matters is that nothing moved
        Assert.InRange(c.R, Thinking.R - 2, Thinking.R + 2);
        Assert.InRange(c.G, Thinking.G - 2, Thinking.G + 2);
        Assert.InRange(c.B, Thinking.B - 2, Thinking.B + 2);
    }

    // "Warmer" has to be stated as a HUE moving toward the hot end, which is the only formulation that
    // holds for every starting colour. Two channel-wise versions of this failed honestly first: lerping a
    // green toward amber RAISES its green channel, and amber toward orange raises its blue one.
    [Fact]
    public void ATighteningSessionWarmsTheFreeHue()
    {
        var calm = Free(Daytime);
        var tight = Free(new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Fx.RgbToHsv(calm, out float h0, out _, out _);
        Fx.RgbToHsv(tight, out float h1, out _, out _);
        Assert.True(h1 < h0, $"hue went {h0:0.#} → {h1:0.#}: not toward the hot end");
        Assert.True(Dist(calm, tight) >= 20, $"{calm} → {tight} is not a visible change");
    }

    [Fact]
    public void ASpentUsageWindowDoesTheSame()
        => Assert.True(Dist(Free(Daytime), Free(new MoodContext(UsageFrac: 0.97f, Hour: 14))) >= 20);

    // the point of a ramp rather than a threshold: halfway up the band is halfway there, so the ring drifts
    // as the session fills instead of flipping colour at one figure
    [Fact]
    public void ItRampsRatherThanSnapping()
    {
        var calm = Free(new MoodContext(ContextFrac: 0.40f, Hour: 14));
        var mid = Free(new MoodContext(ContextFrac: 0.75f, Hour: 14));
        var full = Free(new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Assert.InRange(Dist(calm, mid), 1, Dist(calm, full) - 1);
    }

    [Fact]
    public void ADraggingTurnMovesLessThanRealPressureDoes()
    {
        var calm = Free(Daytime);
        var dragging = Free(new MoodContext(Running: TimeSpan.FromMinutes(15), Hour: 14));
        var tight = Free(new MoodContext(ContextFrac: 0.95f, Hour: 14));
        Assert.True(Dist(calm, dragging) > 0, "a long turn should read differently from a fresh one");
        Assert.True(Dist(calm, dragging) < Dist(calm, tight),
            "a slow turn is not the same news as a full context");
    }

    // red on this ring means a failure. A session merely under pressure must never be able to arrive there,
    // or the one colour that means "something is broken" stops meaning it.
    [Fact]
    public void PressureNeverArrivesAtTheColourThatMeansBroken()
    {
        foreach (var f in new[] { 0.80f, 0.90f, 0.95f, 1.00f })
        {
            var c = Free(new MoodContext(
                ContextFrac: f, UsageFrac: f, Running: TimeSpan.FromHours(1), Hour: 14));
            Assert.True(c.G > Error.G + 25, $"at {f} the ring is {c}, too close to the error red {Error}");
        }
    }

    // the whole rule, in one assertion: an activity hue survives any amount of pressure
    [Fact]
    public void APressuredActivityHueKeepsItsHue()
    {
        foreach (var f in new[] { 0f, 0.85f, 1f })
        {
            var c = Held(new MoodContext(ContextFrac: f, UsageFrac: f,
                Running: TimeSpan.FromHours(1), Hour: 14));
            Fx.RgbToHsv(c, out float h, out _, out _);
            Fx.RgbToHsv(Working, out float h0, out _, out _);
            Assert.InRange(h, h0 - 6f, h0 + 6f);
        }
    }

    [Fact]
    public void TheSmallHoursAreQuieterNotADifferentColour()
    {
        var day = Held(Daytime);
        var night = Held(new MoodContext(Hour: 2));
        Assert.True(night.G < day.G, $"{night} is not quieter than {day}");
        Assert.True(night.G > night.R && night.G > night.B, $"{night} is no longer the working colour");
    }

    // an idle white ring has no hue to move, so this is the case where a bad lerp shows up as grey
    [Fact]
    public void TheIdleRingWarmsWithoutGoingGreyAndKeepsItsAlpha()
    {
        var white = Color.FromArgb(238, 255, 255, 255);
        var tight = Fx.MoodRing(white, new MoodContext(ContextFrac: 0.95f, Hour: 14), hueIsFree: true);
        Assert.Equal(white.A, tight.A);
        Assert.True(tight.R >= tight.G && tight.G > tight.B, $"{tight} is not a warm white");
        Assert.True(tight.R > 200, $"{tight} lost its brightness");
    }
}
