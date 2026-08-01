using Halo.ClaudeCode;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The figure beside a running compact. Two wrong answers came before this one: elapsed against the
// previous compact's duration (a progress bar for something that reports no progress) and then the
// context fill (real, but a different question). What is read now is the summary's streamed tokens off
// the agent's own terminal - the same counter the spinner is showing - so these pin the parsing and the
// one honest weak point, which is what that count is divided by.
public class CompactFigureTests
{
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

    // No previous compact to measure against means no percentage - the reading itself is shown, because
    // it is real and it moves, and a percentage over an invented total would not be.
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
