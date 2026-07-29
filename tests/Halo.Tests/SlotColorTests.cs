using System;
using System.Drawing;
using System.Linq;
using Halo.Agents;
using Halo.ClaudeCode;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The ring's colour and the pill's words now come off the SAME slot, which is the whole point: whatever
// the pill is saying, the ring is the colour of that. So the things worth pinning are that the mapping is
// one mapping, that the colours are actually distinguishable from each other, and that the warm
// modulation no longer washes them all into one orange.
public class SlotColorTests
{
    private static readonly string[] Slots =
    {
        "running", "reading", "writing", "patching", "digging", "fetching", "searching",
        "delegating", "consulting", "planning", "plotting", "skill", "reviewing", "publishing",
        "watching", "asking", "unknown", "peeking", "compacting",
    };

    // compacting is exempt from the pressure modulation in both widgets (RingIsTheMessage): a running
    // compact owns the pill's colour while it lasts, so putting it through MoodRing here would be testing a
    // path that never executes — and it fails, because a dimmed blue does drift toward cyan.
    private static readonly string[] Modulated =
        Slots.Where(s => s != "compacting").ToArray();

    // every slot the widgets can route to must have words AND a colour; a slot with one and not the other
    // is how the two drift apart
    [Fact]
    public void EverySlotTheWidgetsUseHasBothWordsAndAColour()
    {
        foreach (var slot in Slots)
        {
            Assert.NotEmpty(Moods.Set(slot));
            Assert.NotEqual(Color.Empty, Fx.SlotColor(slot));
        }
    }

    // Both widgets' tool maps must only ever name a slot that exists. A typo here would show as the pill
    // silently falling back to "hmm…" in green for a tool it actually understands.
    [Theory]
    [InlineData("Edit")]
    [InlineData("Read")]
    [InlineData("Bash")]
    [InlineData("Grep")]
    [InlineData("WebSearch")]
    [InlineData("Task")]
    [InlineData("TodoWrite")]
    [InlineData("Skill")]
    [InlineData("AskUserQuestion")]
    [InlineData("Monitor")]
    [InlineData("ReportFindings")]
    [InlineData("Artifact")]
    [InlineData("mcp__serena__find_symbol")]
    [InlineData(null)]
    public void ClaudeToolsRouteToASlotThatExists(string? tool)
    {
        var slot = ClaudeCodeWidget.ToolSlot(tool);
        Assert.NotNull(slot);
        Assert.NotEmpty(Moods.Set(slot!));
    }

    [Theory]
    [InlineData("exec")]
    [InlineData("apply_patch")]
    [InlineData("read_file")]
    [InlineData("grep")]
    [InlineData("web_search")]
    [InlineData("browser")]
    [InlineData("view_image")]
    [InlineData("update_plan")]
    [InlineData("spawn")]
    [InlineData("wait")]
    [InlineData("mcp.some.server")]
    [InlineData(null)]
    public void CodexToolsRouteToASlotThatExists(string? tool)
    {
        var slot = CodexWidget.ToolSlot(tool);
        Assert.NotNull(slot);
        Assert.NotEmpty(Moods.Set(slot!));
    }

    [Fact]
    public void AnUnknownToolHasNoSlotAndNamesItselfInstead()
    {
        Assert.Null(ClaudeCodeWidget.ToolSlot("SomeToolNobodyMapped"));
        Assert.Equal("sometoolnobodymapped…", Moods.PrettyTool("SomeToolNobodyMapped"));
    }

    // an mcp tool used to reach the pill as "mcp__serena__find_symbol…" — 26 characters of punctuation on
    // a 220px pill. The server is the half that answers "who is doing this".
    [Fact]
    public void AnMcpToolNamesItsServerAndNotItsPunctuation()
    {
        Assert.Equal("serena…", Moods.PrettyTool("mcp__serena__find_symbol"));
        Assert.Equal("read file…", Moods.PrettyTool("read_file"));
        Assert.Equal("hmm…", Moods.PrettyTool(""));
        Assert.True(Moods.PrettyTool(new string('x', 80)).Length <= Moods.MaxWidth);
    }

    // The colours have to be TELLABLE APART on a 20px ring, which a switch statement cannot promise on its
    // own — two entries can drift to the same hue in a later edit and nothing would complain.
    [Fact]
    public void TheColoursAreDistinguishableFromEachOther()
    {
        var seen = Slots.Select(s => (slot: s, c: Fx.SlotColor(s))).ToArray();
        foreach (var a in seen)
            foreach (var b in seen)
            {
                if (a.slot == b.slot || Fx.SlotColor(a.slot) == Fx.SlotColor(b.slot)) continue;
                int d = Math.Abs(a.c.R - b.c.R) + Math.Abs(a.c.G - b.c.G) + Math.Abs(a.c.B - b.c.B);
                Assert.True(d >= 85, $"{a.slot} {a.c} and {b.slot} {b.c} are only {d} apart");
            }
    }

    private static int Dist(Color a, Color b)
        => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    /// <summary>
    /// The invariant that took two attempts to get right. A pull of 0.85 toward orange sent every slot to
    /// the SAME orange under pressure, so warmth erased the thing it annotates; 0.6 sent a squeezed green
    /// to lime, which is the colour of digging — pressure was impersonating another state, which is worse
    /// than erasing one. So: however warm it gets, a slot must stay nearer its own calm colour than any
    /// other slot's, at every pressure and both times of day.
    /// </summary>
    [Fact]
    public void AWarmedSlotNeverLooksLikeADifferentSlot()
    {
        foreach (float f in new[] { 0f, 0.6f, 0.8f, 0.9f, 1f })
            foreach (int hour in new[] { 14, 2 })
                foreach (var slot in Modulated)
                {
                    var ctx = new MoodContext(ContextFrac: f, UsageFrac: f,
                        Running: TimeSpan.FromMinutes(30), Hour: hour);
                    var lit = Fx.MoodRing(Fx.SlotColor(slot), ctx);
                    int own = Dist(lit, Fx.SlotColor(slot));
                    foreach (var other in Modulated)
                    {
                        if (other == slot || Fx.SlotColor(other) == Fx.SlotColor(slot)) continue;
                        Assert.True(own < Dist(lit, Fx.SlotColor(other)),
                            $"at {f:0.00} / {hour}h, {slot} warmed to {lit}: closer to {other} " +
                            $"({Dist(lit, Fx.SlotColor(other))}) than to itself ({own})");
                    }
                }
    }

    // and it still has to be VISIBLE, or the pressure signal is gone
    [Fact]
    public void PressureIsStillVisibleOnEverySlot()
    {
        foreach (var slot in Modulated)
        {
            var calm = Fx.MoodRing(Fx.SlotColor(slot), new MoodContext(Hour: 14));
            var tight = Fx.MoodRing(Fx.SlotColor(slot), new MoodContext(ContextFrac: 1f, Hour: 14));
            Assert.True(Dist(calm, tight) >= 20, $"{slot} barely moves under pressure: {calm} → {tight}");
        }
    }
}
