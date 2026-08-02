using Halo.Widgets;

namespace Halo.Tests;

// The art chase is one chain per widget, so what the chain does when the world moves under it IS the
// feature. The case that was a live bug: a track starting while another track's chase sleeps in its
// delay — the new track's ChaseArt call bounces off _chasing, so if the sleeping chain gives up on the
// epoch mismatch instead of restarting, nobody ever retries the new track's cover and it wears the app
// logo to the end. Restart, never give up, is the contract these pin down.
public class MediaArtChaseTests
{
    [Fact]
    public void A_track_change_with_no_art_restarts_the_chase_instead_of_ending_it()
        => Assert.Equal(MediaWidget.ArtChase.Restart,
                        MediaWidget.Decide(sessionAlive: true, trackMoved: true, hasArt: false));

    [Fact]
    public void Art_landing_ends_the_chase()
        => Assert.Equal(MediaWidget.ArtChase.Done,
                        MediaWidget.Decide(sessionAlive: true, trackMoved: false, hasArt: true));

    [Fact]
    public void A_new_track_that_already_has_art_needs_no_chase()
        => Assert.Equal(MediaWidget.ArtChase.Done,
                        MediaWidget.Decide(sessionAlive: true, trackMoved: true, hasArt: true));

    [Fact]
    public void A_dead_session_ends_the_chase_whatever_else_is_true()
    {
        Assert.Equal(MediaWidget.ArtChase.Done, MediaWidget.Decide(false, false, false));
        Assert.Equal(MediaWidget.ArtChase.Done, MediaWidget.Decide(false, true, false));
    }

    [Fact]
    public void Same_track_still_missing_art_keeps_fetching()
        => Assert.Equal(MediaWidget.ArtChase.Fetch,
                        MediaWidget.Decide(sessionAlive: true, trackMoved: false, hasArt: false));
}
