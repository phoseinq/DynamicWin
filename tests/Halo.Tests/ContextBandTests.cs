using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

public class ContextBandTests
{
    [Theory]
    [InlineData(0.00f, 0)]
    [InlineData(0.13f, 0)]   // the everyday case: plenty of room, stays blue
    [InlineData(0.64f, 0)]
    [InlineData(0.65f, 1)]   // the approach begins exactly 15 points below the warn line
    [InlineData(0.79f, 1)]
    [InlineData(0.80f, 2)]   // the line itself is already red, not merely approaching
    [InlineData(1.00f, 2)]
    public void TheRampChangesBandAtTheDocumentedThresholds(float frac, int expected)
        => Assert.Equal(expected, ClaudeCodeWidget.ContextBand(frac));

    // the figure turning red and the compact banner firing must be the same event, or the panel would be
    // telling the user one thing while the notification told them another
    [Fact]
    public void RedBeginsExactlyWhereTheBannerFires()
    {
        Assert.Equal(2, ClaudeCodeWidget.ContextBand(ClaudeCodeWidget.ContextWarnAt));
        Assert.Equal(1, ClaudeCodeWidget.ContextBand(ClaudeCodeWidget.ContextWarnAt - 0.001f));
    }

    // Reported live: "86% used" in red inside a ring that was still blue. The arc took a hardcoded Blue
    // while the figure ran the band ramp, so one value wore two colours. Both read this now, and the
    // point of the test is that the ring cannot drift away from the number again.
    [Theory]
    [InlineData(0.13)]
    [InlineData(0.70)]
    [InlineData(0.86)]
    [InlineData(1.00)]
    public void TheArcTakesTheSameColourAsTheFigure(double frac)
    {
        var band = ClaudeCodeWidget.ContextBand((float)frac);
        var expected = band switch
        {
            2 => System.Drawing.Color.FromArgb(229, 72, 77),    // Red
            1 => System.Drawing.Color.FromArgb(255, 176, 32),   // Amber
            _ => System.Drawing.Color.FromArgb(91, 157, 255),   // Blue
        };
        Assert.Equal(expected, ClaudeCodeWidget.ContextColour(frac));
    }

    // no session is not a reading, so it must not paint a warning colour by accident
    [Fact]
    public void NoSessionIsNeverAWarningColour()
        => Assert.Equal(System.Drawing.Color.FromArgb(91, 157, 255), ClaudeCodeWidget.ContextColour(-1));
}
