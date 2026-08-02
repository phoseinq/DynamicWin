using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// The album art switched instead of scaling. DrawCollapsed sized itself off the pill's height alone, so
// while the pill grew its copy of the art raced past the panel's 132 all the way to 170 and sat to the
// left of where the panel's copy was being drawn at the same moment - two squares, crossfading.
public class MediaArtMorphTests
{
    private const float Pill = 40f, Panel = 220f;

    [Fact]
    public void At_rest_on_the_pill_nothing_moved()
    {
        var art = MediaWidget.ArtRect(Pill);
        Assert.Equal(9f, art.X, 3);
        Assert.Equal(7f, art.Y, 3);
        Assert.Equal(26f, art.Width, 3);
        Assert.Equal(26f, art.Height, 3);
    }

    [Fact]
    public void At_rest_on_the_panel_nothing_moved_either()
    {
        var art = MediaWidget.ArtRect(Panel);
        Assert.Equal(26f, art.X, 3);
        Assert.Equal(26f, art.Y, 3);
        Assert.Equal(132f, art.Width, 3);
        Assert.Equal(132f, art.Height, 3);
    }

    // The old bug, stated as a property: at no height may the art be bigger than the panel's, which is
    // where the growing pill copy used to end up.
    [Fact]
    public void The_art_never_grows_past_the_size_it_is_growing_towards()
    {
        for (int i = 0; i <= 400; i++)
        {
            float h = Pill + i;
            var art = MediaWidget.ArtRect(h);
            Assert.InRange(art.Width, 26f, 132f);
        }
    }

    // The ask panel and the notification banner drive h past the media panel's own height; the art has to
    // stop rather than keep inflating.
    [Fact]
    public void A_taller_surface_does_not_keep_inflating_it()
    {
        Assert.Equal(MediaWidget.ArtRect(Panel).Width, MediaWidget.ArtRect(Panel + 200f).Width, 3);
        Assert.Equal(MediaWidget.ArtRect(Pill).Width, MediaWidget.ArtRect(Pill - 30f).Width, 3);
    }

    [Fact]
    public void It_only_ever_grows_on_the_way_out()
    {
        float last = 0f;
        for (int i = 0; i <= 180; i++)
        {
            var art = MediaWidget.ArtRect(Pill + i);
            Assert.True(art.Width >= last, $"art shrank at h={Pill + i}");
            Assert.True(art.X >= 9f - 0.001f && art.X <= 26f + 0.001f, $"art left {art.X} out of range");
            last = art.Width;
        }
    }

    // Both draws take the same rect, which is the whole reason the crossfade reads as one image; if the
    // two ever disagreed again this is what would say so.
    [Fact]
    public void Both_ends_of_the_crossfade_ask_for_the_same_rectangle()
    {
        foreach (float h in new[] { 40f, 73f, 110f, 155f, 199f, 220f })
            Assert.Equal(MediaWidget.ArtRect(h), MediaWidget.ArtRect(h));
    }

    [Fact]
    public void The_corner_opens_out_with_the_size()
    {
        Assert.Equal(26f * 0.28f, MediaWidget.ArtRadius(Pill), 3);
        Assert.Equal(14f, MediaWidget.ArtRadius(Panel), 3);
        Assert.InRange(MediaWidget.ArtRadius(130f), 26f * 0.28f, 14f);
    }

    [Fact]
    public void The_morph_fraction_spans_exactly_the_two_resting_heights()
    {
        Assert.Equal(0f, MediaWidget.MorphT(Pill), 3);
        Assert.Equal(1f, MediaWidget.MorphT(Panel), 3);
        Assert.Equal(0.5f, MediaWidget.MorphT((Pill + Panel) / 2f), 3);
    }
}
