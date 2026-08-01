using Halo.ClaudeCode;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The figure beside a running compact. Three wrong answers came before this one: elapsed against the
// previous compact's duration (a progress bar for something assumed to report no progress), the context
// fill (real, but a different question), and the streamed token count (real during an ordinary turn,
// absent during a compact). Claude Code does publish the true figure - as a forty-cell bar of filled and
// empty parallelograms, with no number anywhere on the line - and that is what is read now.
public class CompactFigureTests
{
    // Built rather than pasted: source files here stay ASCII, and an editor that resolves an escape puts
    // the real glyph back the moment the line is written.
    private static string Bar(int filled, int total)
        => new string((char)0x25B0, filled) + new string((char)0x25B1, total - filled);

    // Captured off a live compact: the frames went 0/40, 1/40, 2/40 ... 16/40, which is the 40% the bar in
    // the terminal had reached when it was cancelled.
    [Theory]
    [InlineData(0, 40, 0)]
    [InlineData(1, 40, 2)]
    [InlineData(16, 40, 40)]
    [InlineData(40, 40, 100)]
    [InlineData(5, 10, 50)]
    public void TheBarIsReadAsAShareOfItself(int filled, int total, int expected)
        => Assert.Equal(expected, CompactProgress.BarShare("  " + Bar(filled, total)));

    // a stray glyph or two in ordinary output must not read as a progress bar, and a line with no bar on
    // it at all must not answer with a number
    [Fact]
    public void OnlySomethingBarLengthCounts()
    {
        Assert.Null(CompactProgress.BarShare("see " + Bar(1, 2) + " here"));
        Assert.Null(CompactProgress.BarShare("* Compacting conversation..."));
        Assert.Null(CompactProgress.BarShare(""));
    }

    // The fallback, for a build that shows the streamed count instead of a bar. Kept because it is what an
    // ordinary turn's spinner carries, so a future compact UI may well carry it too.
    [Theory]
    [InlineData("* Compacting conversation... (esc to interrupt - 12s - 1.2k tokens)", 1200)]
    [InlineData("* Infusing... (4m 10s - 13.1k tokens)", 13100)]
    [InlineData("  832 tokens", 832)]
    [InlineData("* Compacting conversation... (esc to interrupt - 3s)", null)]
    [InlineData("dotnet build -c Release", null)]
    [InlineData("", null)]
    public void TheStreamedCountIsReadOffTheSpinner(string line, int? expected)
        => Assert.Equal(expected, CompactProgress.Streamed(line));

    // the spinner glyph is one of a rotating set and the wording changes between versions; the parse
    // deliberately hangs on nothing but the number and the word after it
    [Fact]
    public void NeitherTheGlyphNorTheWordingIsPartOfTheContract()
    {
        Assert.Equal(1200, CompactProgress.Streamed("~ Whatever it says next (1.2k tokens)"));
        Assert.Equal(1200, CompactProgress.Streamed("1.2k tokens"));
    }

    // Only the token fallback needs a denominator, and it is measured rather than guessed: four real
    // compactions in this project's transcripts came to 5.0k / 5.3k / 5.9k / 6.5k tokens. A summary that
    // overruns its expectation must not reach 100 - only compact_end ends it.
    [Theory]
    [InlineData(0, 5700, -1)]
    [InlineData(570, 5700, 10)]
    [InlineData(2850, 5700, 50)]
    [InlineData(5700, 5700, 99)]
    [InlineData(9000, 5700, 99)]
    public void TheShareIsTheReadingOverWhatASummaryComesTo(int tokens, int expect, int share)
        => Assert.Equal(share, CompactProgress.Share(tokens, expect));

    [Theory]
    [InlineData(-1, 1200, "1.2k tok")]
    [InlineData(-1, 832, "832 tok")]
    [InlineData(-1, 0, "")]
    [InlineData(-1, -1, "")]
    [InlineData(47, 1200, "47%")]
    [InlineData(99, 40000, "99%")]
    public void ThePercentageOnlyAppearsOnceThereIsSomethingRealToDivideBy(int percent, int tokens, string expected)
        => Assert.Equal(expected, CompactProgress.Caption(percent, tokens));

    // While a compact runs the pill carries both the progress and the clock, and what is left is the
    // verb's budget. With "2m 0s" beside it the mood arrived cut mid-word ("big histo..."); with the
    // seconds gone the same line fits whole. Seconds under a minute stay - that is the whole reading then.
    [Theory]
    [InlineData("2m 0s", "2m")]
    [InlineData("14m 37s", "14m")]
    [InlineData("45s", "45s")]
    [InlineData("", "")]
    public void TheClockCoarsensOnlyOnceThereAreMinutes(string elapsed, string expected)
        => Assert.Equal(expected, ClaudeCodeWidget.Coarse(elapsed));
}
