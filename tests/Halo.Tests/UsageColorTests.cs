using System.Drawing;
using Halo.Widgets;

namespace Halo.Tests;

// The usage bar lerped blue to amber component-wise, and those two average to (163,165,157) — so a bar
// sitting anywhere near 60% rendered grey and read as disabled instead of as warming up. The ramp now
// rotates hue, and these pin it: every step of it has to stay coloured.
public class UsageColorTests
{
    private static float Saturation(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
        return max == 0 ? 0f : (max - min) / (float)max;
    }

    [Theory]
    [InlineData(0.55f)]
    [InlineData(0.61f)]  // the value that rendered grey
    [InlineData(0.65f)]
    [InlineData(0.70f)]
    [InlineData(0.75f)]
    [InlineData(0.85f)]
    [InlineData(0.95f)]
    public void Never_washes_out_to_grey(float frac)
        => Assert.True(Saturation(ClaudeCodeWidget.UsageColorForTest(frac)) > 0.35f,
            $"usage bar at {frac:P0} is {ClaudeCodeWidget.UsageColorForTest(frac)}, " +
            $"saturation {Saturation(ClaudeCodeWidget.UsageColorForTest(frac)):0.00}");

    [Fact]
    public void Stays_blue_while_there_is_plenty_left()
        => Assert.Equal(Color.FromArgb(91, 157, 255), ClaudeCodeWidget.UsageColorForTest(0.30f));

    [Fact]
    public void Ends_on_red_when_the_window_is_spent()
    {
        var c = ClaudeCodeWidget.UsageColorForTest(1f);
        Assert.True(c.R > 200 && c.G < 110 && c.B < 110, $"expected red at 100%, got {c}");
    }

    // hue has to move monotonically down the wheel (blue 217 -> amber 39), which is what keeps the ramp
    // reading as one continuous warming rather than a set of steps
    [Fact]
    public void Warms_monotonically_across_the_blue_to_amber_span()
    {
        float last = 361f;
        for (float f = 0.5f; f <= 0.75f; f += 0.05f)
        {
            var c = ClaudeCodeWidget.UsageColorForTest(f);
            int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
            float d = max - min;
            float h = d == 0 ? 0
                : max == c.R ? 60f * (((c.G - c.B) / d + 6f) % 6f)
                : max == c.G ? 60f * ((c.B - c.R) / d + 2f)
                : 60f * ((c.R - c.G) / d + 4f);
            Assert.True(h <= last + 0.5f, $"hue went back up at {f:P0}: {h:0.#} after {last:0.#}");
            last = h;
        }
    }
}
