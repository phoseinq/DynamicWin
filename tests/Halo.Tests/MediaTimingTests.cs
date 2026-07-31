using Halo.Widgets;

namespace Halo.Tests;

// Every rule here failed as a *rendering* bug first: a bar that leapt forward at the start of a track and fell
// back, a seek that kept re-landing for seconds after the tap, a fill frozen mid-video, a pill with no bar at
// all. None of that is reachable from a screenshot, and one of these rules was measured wrong after it looked
// obviously right, so they get tested away from any player.
public class MediaTimingTests
{
    // ── when a seek is spoken ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Isolated_seek_is_sent_at_once()
    {
        // one drag, one release, nothing sent for ages: waiting out a burst that isn't happening is pure
        // latency charged to the common case
        Assert.Equal(MediaTiming.SeekStep.Send, MediaTiming.NextSeekStep(tries: 0, msSinceAsked: 0, msSinceSent: 60_000));
    }

    [Fact]
    public void Tap_landing_just_after_a_send_waits_for_the_tapping_to_stop()
    {
        Assert.Equal(MediaTiming.SeekStep.Wait, MediaTiming.NextSeekStep(tries: 0, msSinceAsked: 80, msSinceSent: 200));
    }

    [Fact]
    public void Burst_is_spoken_once_it_has_gone_quiet()
    {
        Assert.Equal(MediaTiming.SeekStep.Send, MediaTiming.NextSeekStep(tries: 0, msSinceAsked: 400, msSinceSent: 500));
    }

    [Fact]
    public void First_retry_waits_then_goes()
    {
        Assert.Equal(MediaTiming.SeekStep.Wait, MediaTiming.NextSeekStep(tries: 1, msSinceAsked: 400, msSinceSent: 300));
        Assert.Equal(MediaTiming.SeekStep.Send, MediaTiming.NextSeekStep(tries: 1, msSinceAsked: 900, msSinceSent: 800));
    }

    [Fact]
    public void It_stops_asking_after_one_retry()
    {
        // a player that has stopped reporting will never agree however often it is asked, and each retry
        // seeks the video again - which is what made a seek keep re-landing for seconds
        Assert.Equal(MediaTiming.SeekStep.GiveUp, MediaTiming.NextSeekStep(tries: 2, msSinceAsked: 900, msSinceSent: 800));
    }

    [Fact]
    public void It_gives_up_on_time_even_mid_backoff()
    {
        Assert.Equal(MediaTiming.SeekStep.GiveUp, MediaTiming.NextSeekStep(tries: 1, msSinceAsked: 4000, msSinceSent: 100));
    }

    [Fact]
    public void Whole_exchange_is_bounded_to_a_couple_of_seconds()
    {
        Assert.True(MediaTiming.GiveUpMs <= 2500);
        Assert.True(MediaTiming.MaxTries <= 2);
    }

    // ── which timeline reports are believed ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_outgoing_tracks_timeline_is_ignored_briefly_after_a_track_change()
    {
        var old = TimeSpan.FromMinutes(3);
        Assert.True(MediaTiming.IsLeftover(incomingEnd: old, prevEnd: old, msSinceTrack: 300));
    }

    [Fact]
    public void A_different_duration_means_the_new_tracks_timeline_has_landed()
    {
        Assert.False(MediaTiming.IsLeftover(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3), 300));
    }

    [Fact]
    public void Equal_length_tracks_cannot_stall_the_bar_forever()
    {
        var same = TimeSpan.FromMinutes(3);
        Assert.False(MediaTiming.IsLeftover(same, same, MediaTiming.LeftoverMs + 1));
    }

    [Fact]
    public void With_no_previous_track_nothing_is_held_back()
    {
        // attaching to a session is not a track change; holding the span Hook() just read cost 2s of no bar
        Assert.False(MediaTiming.IsLeftover(TimeSpan.FromMinutes(3), TimeSpan.Zero, 0));
    }

    [Fact]
    public void A_zeroed_span_never_erases_a_duration_already_known()
    {
        // a backgrounded browser tab answers with zeros; taken literally the bar vanishes for the rest of
        // the video
        Assert.True(MediaTiming.IsBlank(TimeSpan.Zero, TimeSpan.Zero,
                                        knownStart: TimeSpan.Zero, knownEnd: TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void A_live_stream_with_no_duration_is_not_treated_as_a_refusal()
    {
        // it reports zero from the very first update, has no span to keep, and correctly gets no bar
        Assert.False(MediaTiming.IsBlank(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
    }

    [Fact]
    public void A_real_span_is_always_taken()
    {
        Assert.False(MediaTiming.IsBlank(TimeSpan.Zero, TimeSpan.FromMinutes(4),
                                         TimeSpan.Zero, TimeSpan.FromMinutes(3)));
    }

    // ── the clock behind the extrapolation ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_repeated_position_while_playing_does_not_restart_the_clock()
    {
        // re-stamping against an unchanged position restarts extrapolation every poll, so the fill can never
        // grow past one poll's worth and the bar sits frozen while the video plays
        Assert.False(MediaTiming.ShouldRestamp(repeated: true, playing: true, confirming: false));
    }

    [Fact]
    public void The_report_that_confirms_a_seek_always_restarts_the_clock()
    {
        Assert.True(MediaTiming.ShouldRestamp(repeated: true, playing: true, confirming: true));
    }

    [Fact]
    public void A_moving_position_restarts_the_clock()
    {
        Assert.True(MediaTiming.ShouldRestamp(repeated: false, playing: true, confirming: false));
    }

    [Fact]
    public void Paused_media_is_stamped_normally()
    {
        // nothing is being extrapolated while paused, so there is no freeze to protect against
        Assert.True(MediaTiming.ShouldRestamp(repeated: true, playing: false, confirming: false));
    }
}
