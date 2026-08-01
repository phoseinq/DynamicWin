using Halo.ClaudeCode;
using Halo.Codex;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The figure beside a running compact used to be elapsed/expected: a progress reading for something that
// reports no progress, invented from the PREVIOUS compact's duration. What is shown now is the one number
// that is real at that moment - how full the context is - and these pin the two properties that make it
// worth showing at all: it is the SAME number the ring and the "context NN% full" banner carry, and it is
// absent rather than guessed when there is no session to read.
public class CompactFigureTests
{
    private static CcStatus Ctx(long used, long max) =>
        new() { State = "compacting", Session = new CcSession { ContextUsed = used, ContextMax = max } };

    [Theory]
    [InlineData(0, 200_000, "ctx 0%")]
    [InlineData(126_731, 1_000_000, "ctx 12%")]
    [InlineData(170_000, 200_000, "ctx 85%")]
    [InlineData(200_000, 200_000, "ctx 100%")]
    public void TheCompactingPillReadsTheRealContextFill(long used, long max, string expected)
        => Assert.Equal(expected, ClaudeCodeWidget.ContextPct(Ctx(used, max)));

    // Truncated, not rounded, because the banner and the ring truncate: 84.9% must not read as 85 in one
    // place and 84 in another when the whole point is that they agree.
    [Fact]
    public void ItTruncatesTheWayTheBannerDoes()
    {
        Assert.Equal("ctx 84%", ClaudeCodeWidget.ContextPct(Ctx(169_999, 200_000)));
        Assert.Equal((int)(169_999 / 200_000.0 * 100) + "%", ClaudeCodeWidget.ContextPct(Ctx(169_999, 200_000))[4..]);
    }

    // No session, no window, nothing to divide by: the pill says nothing rather than 0% or a guess.
    [Fact]
    public void NothingKnownShowsNothing()
    {
        Assert.Equal("", ClaudeCodeWidget.ContextPct(new CcStatus { State = "compacting" }));
        Assert.Equal("", ClaudeCodeWidget.ContextPct(Ctx(50_000, 0)));
    }

    // Both figures share the right-hand end of a 220px pill, and what is left over is the verb's budget.
    // With "2m 0s" beside the fill, the mood arrived cut mid-word ("big histo..."); with the seconds gone
    // the same line fits whole. Seconds under a minute are kept - that is the whole reading at that point.
    [Theory]
    [InlineData("2m 0s", "2m")]
    [InlineData("14m 37s", "14m")]
    [InlineData("45s", "45s")]
    [InlineData("", "")]
    public void TheClockCoarsensOnlyOnceThereAreMinutes(string elapsed, string expected)
        => Assert.Equal(expected, ClaudeCodeWidget.Coarse(elapsed));

    private static CodexSnapshot Codex(long used, long max) => new(
        CodexSurface.Cli, "compacting", null, System.DateTimeOffset.UtcNow, null, null, null, 7, 7,
        used, max, 0, null, null, System.DateTimeOffset.UtcNow, true);

    // Codex is the twin and gets the same treatment; its window is real too (model_context_window out of
    // the rollout), which is what made the 180-second pacing it used to print indefensible.
    [Fact]
    public void TheCodexPillReadsItsOwnWindow()
    {
        Assert.Equal("ctx 40%", CodexWidget.ContextPct(Codex(108_800, 272_000)));
        Assert.Equal("", CodexWidget.ContextPct(Codex(108_800, 0)));
    }
}
