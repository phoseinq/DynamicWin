using Halo.Widgets;

namespace Halo.Tests;

// The cover flip is a horizontal squeeze standing in for a card turning: the OLD face shows while the
// card narrows, the NEW face from the zero crossing on. The geometry is the testable part; the swap
// happening exactly at the narrowest instant is what makes it read as "the next one comes from behind".
public class MediaFlipTests
{
    [Fact]
    public void It_starts_and_ends_at_full_width()
    {
        Assert.Equal(1f, MediaWidget.FlipPose(0f).sx, precision: 4);
        Assert.Equal(1f, MediaWidget.FlipPose(1f).sx, precision: 4);
    }

    [Fact]
    public void The_face_swaps_at_the_narrowest_instant()
    {
        Assert.True(MediaWidget.FlipPose(0.49f).front);
        Assert.False(MediaWidget.FlipPose(0.5f).front);
        var (sx, _) = MediaWidget.FlipPose(0.5f);
        Assert.True(sx <= 0.01f, $"crossing width {sx} is visible");
    }

    [Fact]
    public void The_width_never_degenerates_to_zero()
    {
        for (float t = 0f; t <= 1f; t += 0.05f)
            Assert.True(MediaWidget.FlipPose(t).sx >= 0.001f);
    }
}
