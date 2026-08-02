using Halo.Widgets;

namespace Halo.Tests;

// The caption used to be a three-entry table (300, 10080, else "plan"), so a plan whose buckets are
// any other length produced two rows both captioned "plan" — and, worse, the projections feeding them
// were named FiveHour/Week without ever checking the duration, so the pill claimed a 5-hour window
// Codex does not necessarily have. These pin the caption to the real window length.
public sealed class CodexLimitCaptionTests
{
    [Theory]
    [InlineData(300, "5-hour")]
    [InlineData(60, "1-hour")]
    [InlineData(720, "12-hour")]
    [InlineData(30, "30-min")]
    [InlineData(90, "1h30m")]
    public void SubDayWindowsNameTheirLength(int minutes, string expected)
        => Assert.Equal(expected, CodexWidget.LimitCaption(minutes));

    // seven days keeps the word rather than "7-day": it is the one length a reader recognises by name
    [Fact]
    public void SevenDaysStaysWeekly() => Assert.Equal("weekly", CodexWidget.LimitCaption(10_080));

    [Theory]
    [InlineData(1440, "1-day")]
    [InlineData(2880, "2-day")]
    [InlineData(20_160, "14-day")]
    [InlineData(1500, "1d1h")]
    public void MultiDayWindowsNameTheirLength(int minutes, string expected)
        => Assert.Equal(expected, CodexWidget.LimitCaption(minutes));

    // no duration reported is the one case where inventing a name would be a lie
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnreportedWindowStaysGeneric(int minutes)
        => Assert.Equal("plan", CodexWidget.LimitCaption(minutes));

    // the whole point: two buckets of different lengths can no longer collide on one caption
    [Fact]
    public void TwoDifferentWindowsGetDifferentCaptions()
        => Assert.NotEqual(CodexWidget.LimitCaption(10_080), CodexWidget.LimitCaption(4320));
}
