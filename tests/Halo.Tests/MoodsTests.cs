using System;
using System.Linq;
using Halo.Agents;
using Xunit;

namespace Halo.Tests;

// The table is written by hand and read on the render path, so the things that can go wrong with it
// are all shape: a line too long for the pill, an empty set, a duration band with no slot behind it.
// None of those would throw - they would just look broken on a widget that cannot be screenshotted.
public class MoodsTests
{
    [Fact]
    public void NoLineCanBeClippedByThePill()
    {
        var tooLong = Moods.Keys
            .SelectMany(k => Moods.Set(k).Select(l => (k, l)))
            .Where(x => x.l.Length > Moods.MaxWidth)
            .ToArray();
        Assert.True(tooLong.Length == 0,
            "over " + Moods.MaxWidth + " chars: " + string.Join(", ", tooLong.Select(x => $"{x.k}/\"{x.l}\"")));
    }

    [Fact]
    public void EverySetHasLines()
        => Assert.All(Moods.Keys, k => Assert.NotEmpty(Moods.Set(k)));

    [Fact]
    public void NoSetRepeatsALine()
        => Assert.All(Moods.Keys, k => Assert.Equal(Moods.Set(k).Length, Moods.Set(k).Distinct().Count()));

    // "@long" is a band ON a slot. One without its base slot would be unreachable, since Line() only
    // looks for the band after resolving the slot.
    [Fact]
    public void EveryLongBandHasASlotBehindIt()
    {
        foreach (var k in Moods.Keys.Where(k => k.Contains('@')))
            Assert.NotEmpty(Moods.Set(k.Substring(0, k.IndexOf('@'))));
    }

    [Fact]
    public void AnUnknownSlotFallsBackInsteadOfThrowing()
    {
        Assert.Equal("hmm…", Moods.Fixed("no-such-slot"));
        Assert.Equal("hmm…", Moods.Line("no-such-slot"));
    }

    // the shipped wording has to stay reachable: it is the line the product was designed around
    [Fact]
    public void TheOriginalLineIsFirstInItsSet()
    {
        Assert.Equal("let's work :)", Moods.Fixed("idle"));
        Assert.Equal("googling :P", Moods.Fixed("searching"));
        Assert.Equal("hmm…", Moods.Fixed("unknown"));
        Assert.Equal("hmm…", Moods.Fixed("unknown@long"));   // the band falls back to its slot
    }

    [Fact]
    public void ALineHoldsSteadyWhileTheStateDoes()
    {
        // Draw* runs up to 120 times a second; a fresh roll per call would strobe the text
        var first = Moods.Line("digging");
        Assert.All(Enumerable.Range(0, 50), _ => Assert.Equal(first, Moods.Line("digging")));
    }

    // Reported live: the pill said "still cooking…" and nothing else, ever. The hold was being re-stamped
    // on every READ, which makes it a sliding window - and Draw* reads it 125 times a second, so any key
    // the pill kept looking at could never expire and the whole table was decoration. It has to do both
    // things: hold steady under a flood of reads, and still move on once the minute is actually up.
    [Fact]
    public void AHeldLineExpiresOnItsOwnClockAndNotOnTheLastLook()
    {
        var t0 = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        var first = Moods.Line("running", new MoodContext(), t0);
        foreach (var _ in Enumerable.Range(0, 500))
            Assert.Equal(first, Moods.Line("running", new MoodContext(), t0.AddSeconds(30)));
        Assert.NotEqual(first, Moods.Line("running", new MoodContext(), t0.AddSeconds(61)));
    }

    // …and the reroll has to actually land somewhere else. With a two-line set, picking at random means a
    // coin flip every minute on whether anything appears to have happened.
    [Fact]
    public void ARerollDoesNotLandOnTheLineThatJustExpired()
    {
        foreach (var k in Moods.Keys.Where(k => Moods.Set(k).Length > 1))
            foreach (var l in Moods.Set(k))
                Assert.NotEqual(l, Moods.Pick(k, avoid: l));
    }

    [Fact]
    public void PastTheGraceItSpeaksFromTheLongSet()
    {
        var line = Moods.Line("running", TimeSpan.FromMinutes(3));
        Assert.Contains(line, Moods.Set("running@long"));
    }

    [Fact]
    public void AndFromTheAgesSetOnceItIsReallyDraggingOn()
    {
        var line = Moods.Line("running", TimeSpan.FromMinutes(20));
        Assert.Contains(line, Moods.Set("running@ages"));
    }

    // A slot with an @long but no @ages must keep saying the @long thing rather than snapping back to
    // the plain wording - the bands only ever escalate.
    [Fact]
    public void AMissingTopBandFallsToTheOneBelowNotToTheBottom()
    {
        foreach (var k in Moods.Keys.Where(k => k.EndsWith("@long")))
        {
            var slot = k.Substring(0, k.IndexOf('@'));
            if (Moods.Set(slot + "@ages").Length > 0) continue;
            var line = Moods.Line(slot, TimeSpan.FromHours(1));
            Assert.Contains(line, Moods.Set(k));
        }
    }

    [Fact]
    public void ASlotWithNoLongSetJustKeepsItsOwnWording()
    {
        var line = Moods.Line("idle", TimeSpan.FromHours(3));
        Assert.Contains(line, Moods.Set("idle"));
    }

    // The ladder is the whole of the situational logic: which of several true things gets to speak. Pinned
    // as a table because that order is a design decision - each row here is set up so everything BELOW it
    // is also true, so a reordering breaks exactly one assertion and names itself.
    [Fact]
    public void TheMostPressingSituationIsTheOneThatSpeaks()
    {
        Assert.Equal("", Moods.Modifier(new MoodContext()));
        Assert.Equal("@tight", Moods.Modifier(new MoodContext(
            ContextFrac: Moods.TightAt, UsageFrac: 1f, Running: TimeSpan.FromHours(1),
            ToolRuns: 99, PromptTokens: long.MaxValue, Hour: 3)));
        Assert.Equal("@thin", Moods.Modifier(new MoodContext(
            UsageFrac: Moods.ThinAt, Running: TimeSpan.FromHours(1), ToolRuns: 99, Hour: 3)));
        Assert.Equal("@ages", Moods.Modifier(new MoodContext(
            Running: TimeSpan.FromMinutes(9), ToolRuns: 99, Hour: 3)));
        Assert.Equal("@long", Moods.Modifier(new MoodContext(
            Running: TimeSpan.FromMinutes(3), ToolRuns: 99, Hour: 3)));
        Assert.Equal("@again", Moods.Modifier(new MoodContext(
            ToolRuns: Moods.AgainAfter, PromptTokens: long.MaxValue, Hour: 3)));
        Assert.Equal("@heavy", Moods.Modifier(new MoodContext(
            PromptTokens: Moods.HeavyTokens, Hour: 3)));
        Assert.Equal("@late", Moods.Modifier(new MoodContext(Hour: 3)));
        Assert.Equal("@early", Moods.Modifier(new MoodContext(Hour: 6)));
        Assert.Equal("", Moods.Modifier(new MoodContext(Hour: 14)));
    }

    // midnight is hour 0, so a context that knows nothing must not read as the middle of the night
    [Fact]
    public void AnEmptyContextIsSilentRatherThanNocturnal()
    {
        Assert.Null(new MoodContext().Hour);
        Assert.Equal("", Moods.Modifier(default));
    }

    [Fact]
    public void ASituationWithNothingWrittenForItFallsToTheNextOneDown()
    {
        // "fetching" has no @tight set, and a nearly-full context must not silence it: it keeps the band
        // it does have rather than snapping back to the plain wording
        var ctx = new MoodContext(Running: TimeSpan.FromMinutes(20), ContextFrac: 0.99f);
        Assert.Equal("@tight", Moods.Modifier(ctx));
        Assert.Empty(Moods.Set("fetching@tight"));
        Assert.Contains(Moods.Line("fetching", ctx), Moods.Set("fetching@ages"));
    }

    [Fact]
    public void AFullContextOutranksALongTurn()
    {
        var ctx = new MoodContext(Running: TimeSpan.FromMinutes(20), ContextFrac: 0.9f);
        Assert.Contains(Moods.Line("running", ctx), Moods.Set("running@tight"));
    }

    [Fact]
    public void TheTimeOfDayOnlySpeaksWhenNothingElseDoes()
    {
        Assert.Contains(Moods.Line("idle", new MoodContext(Hour: 2)), Moods.Set("idle@late"));
        Assert.Contains(Moods.Line("idle", new MoodContext(Hour: 2, ContextFrac: 0.95f)),
            Moods.Set("idle@tight"));
    }

    [Fact]
    public void EveryLineIsPrintableAscii()
    {
        // the pill draws with Segoe UI and no fallback chain worth relying on; the ellipsis is the one
        // non-ascii character the design actually uses, and mojibake has reached the screen before
        foreach (var k in Moods.Keys)
            foreach (var l in Moods.Set(k))
                Assert.All(l, c => Assert.True(c == '…' || (c >= ' ' && c <= '~'),
                    $"{k}: unexpected character U+{(int)c:X4} in \"{l}\""));
    }

    // "idle" was a line in the idle set. It is the raw state name - the one thing this table exists to avoid
    // saying out loud - and it read as a debug string on the pill. It was also the shortest line in its set,
    // so it won every time the space was tight. A verb with an ellipsis ("working…") is voice; a bare state
    // name is a leak.
    [Fact]
    public void NoLineIsJustTheNameOfAState()
    {
        var states = new[] { "idle", "working", "compacting", "waiting_input", "unknown", "error" };
        foreach (var key in Moods.Keys)
            foreach (var line in Moods.Set(key))
                Assert.DoesNotContain(line.Trim().ToLowerInvariant(), states);
    }
}
