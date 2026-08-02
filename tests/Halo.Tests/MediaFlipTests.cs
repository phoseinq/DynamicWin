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

// The flip's trigger identity: a cover is its bytes. A chase retry re-committing the bytes already on
// screen used to replay a full flip with identical faces - "the animation comes but the art doesn't
// change" - so EnsureArt now compares ThumbHash before starting the card.
public class MediaArtHashTests
{
    [Fact]
    public void No_art_hashes_to_zero()
    {
        Assert.Equal(0, MediaWidget.ThumbHash(null));
        Assert.Equal(0, MediaWidget.ThumbHash(System.Array.Empty<byte>()));
    }

    [Fact]
    public void Same_bytes_same_hash_different_bytes_different_hash()
    {
        var a = new byte[] { 1, 2, 3, 4, 5 };
        var b = new byte[] { 1, 2, 3, 4, 5 };
        var c = new byte[] { 1, 2, 3, 4, 6 };
        Assert.Equal(MediaWidget.ThumbHash(a), MediaWidget.ThumbHash(b));
        Assert.NotEqual(MediaWidget.ThumbHash(a), MediaWidget.ThumbHash(c));
    }

    // 0 is the reserved "no art" value - real content must never collide with it
    [Fact]
    public void Real_content_never_hashes_to_the_no_art_value()
    {
        Assert.NotEqual(0, MediaWidget.ThumbHash(new byte[] { 0 }));
        Assert.NotEqual(0, MediaWidget.ThumbHash(new byte[] { 0, 0, 0 }));
    }
}
