using System;
using Halo.Widgets;

namespace Halo.Tests;

// Telegram's strip shows ONE time text and a percent slider, and nothing labels whether the text is
// elapsed or total. Infer() decides from motion across two samples; these pin that decision, because
// getting it backwards displays a duration as a position - a lie the no-invented-numbers rule exists for.
public class TelegramPlayerTests
{
    private static TimeSpan T(int m, int s) => new(0, m, s);

    [Fact]
    public void Advancing_text_is_elapsed_and_yields_the_duration()
    {
        var (pos, dur) = TelegramPlayer.Infer(0.5, T(0, 50), 0.49, T(0, 49), null);
        Assert.Equal(T(0, 50), pos);
        Assert.Equal(TimeSpan.FromSeconds(100), dur);
    }

    [Fact]
    public void Constant_text_under_a_moving_slider_is_the_total()
    {
        var (pos, dur) = TelegramPlayer.Infer(0.60, T(3, 43), 0.50, T(3, 43), null);
        Assert.Equal(T(3, 43), dur);
        Assert.Equal(Math.Round(0.60 * 223), pos.TotalSeconds, precision: 0);
    }

    [Fact]
    public void A_settled_duration_survives_percent_jitter()
    {
        // 84% of 3:43 shows 3:07; the whole-percent slider makes the estimate wobble a second or two
        var settled = TimeSpan.FromSeconds(223);
        var (_, dur) = TelegramPlayer.Infer(0.84, T(3, 7), 0.83, T(3, 6), settled);
        Assert.Equal(settled, dur);
    }

    [Fact]
    public void Paused_keeps_the_known_duration_and_reads_position_from_the_text()
    {
        var (pos, dur) = TelegramPlayer.Infer(0.5, T(1, 51), 0.5, T(1, 51), TimeSpan.FromSeconds(223));
        Assert.Equal(TimeSpan.FromSeconds(223), dur);
        Assert.Equal(T(1, 51), pos);
    }

    [Fact]
    public void A_lone_first_sample_claims_no_duration()
    {
        var (_, dur) = TelegramPlayer.Infer(0.84, T(3, 7), 0.84, T(3, 7), null);
        Assert.Null(dur);
    }

    [Theory]
    [InlineData("84%", 0.84)]
    [InlineData("0%", 0.0)]
    [InlineData("100%", 1.0)]
    public void Percent_strings_parse(string s, double want)
        => Assert.Equal(want, TelegramPlayer.ParsePercent(s)!.Value, precision: 3);

    [Fact]
    public void Junk_percent_and_time_parse_to_null()
    {
        Assert.Null(TelegramPlayer.ParsePercent("Legendary"));
        Assert.Null(TelegramPlayer.ParsePercent(null));
        Assert.Null(TelegramPlayer.ParseTime("at 1:28"));
        Assert.Null(TelegramPlayer.ParseTime(null));
    }

    [Fact]
    public void Times_parse_in_both_shapes()
    {
        Assert.Equal(TimeSpan.FromSeconds(187), TelegramPlayer.ParseTime("03:07"));
        Assert.Equal(new TimeSpan(1, 2, 3), TelegramPlayer.ParseTime("1:02:03"));
    }

    // telegram's speed button carries its value in its own accessible name, which is what makes showing
    // the speed honest: it is read from the app, never assumed
    [Theory]
    [InlineData("Playback speed: 1x", "1x")]
    [InlineData("Playback speed: 0.5x", "0.5x")]
    [InlineData("Playback speed: 2X", "2x")]
    public void Speed_is_read_from_the_buttons_own_label(string name, string want)
        => Assert.Equal(want, TelegramPlayer.ParseSpeed(name));

    [Theory]
    [InlineData("Playback speed:")]
    [InlineData("Volume")]
    [InlineData(null)]
    public void A_label_with_no_speed_in_it_reads_as_nothing(string? name)
        => Assert.Null(TelegramPlayer.ParseSpeed(name));

    // 2212 is MINUS SIGN. Telegram labels a video's remaining time with the real minus, not an ascii
    // hyphen — measured off the pill's own log, where "[00:03][U+2212 00:16]" parsed as elapsed only and
    // the video surface was never claimed, which is the whole "video is not supported" report.
    private static string Minus => ((char)0x2212).ToString();

    [Fact]
    public void Video_clock_reads_a_real_minus_sign()
    {
        var clock = TelegramPlayer.VideoClock(new[] { "00:03", Minus + "00:16" });
        Assert.NotNull(clock);
        Assert.Equal(T(0, 3), clock!.Value.pos);
        Assert.Equal(T(0, 19), clock.Value.dur);   // elapsed + remaining, exact
    }

    [Fact]
    public void Video_clock_also_accepts_an_ascii_hyphen()
        => Assert.Equal(T(0, 19), TelegramPlayer.VideoClock(new[] { "00:09", "-00:10" })!.Value.dur);

    // a finished video leaves its window standing; claiming it would pin the pill to it instead of
    // handing back to the music telegram has already resumed
    [Fact]
    public void A_finished_video_is_not_a_source()
        => Assert.Null(TelegramPlayer.VideoClock(new[] { "00:19", Minus + "00:00" }));

    [Fact]
    public void One_label_alone_is_not_a_video_clock()
    {
        Assert.Null(TelegramPlayer.VideoClock(new[] { "00:03" }));
        Assert.Null(TelegramPlayer.VideoClock(new[] { "Katy Perry", "03:44" }));
    }

    // the reported jitter: a candidate a second or two off the settled duration must not move the bar's end
    [Fact]
    public void A_wobbling_estimate_does_not_move_a_settled_duration()
    {
        Assert.Equal(T(3, 39), TelegramPlayer.Settle(T(3, 39), T(3, 41), T(1, 0)));
        Assert.Equal(T(3, 39), TelegramPlayer.Settle(T(3, 39), T(3, 37), T(1, 0)));
    }

    [Fact]
    public void A_real_disagreement_replaces_it()
        => Assert.Equal(T(3, 39), TelegramPlayer.Settle(T(2, 26), T(3, 39), T(1, 0)));

    [Fact]
    public void First_candidate_settles()
        => Assert.Equal(T(3, 39), TelegramPlayer.Settle(null, T(3, 39), T(0, 5)));

    // watching a video and coming back to the music mis-read the song's ELAPSED as its total, and pinning
    // that left the bar full for the rest of the track. Walking past the duration must un-settle it.
    [Fact]
    public void A_duration_the_track_has_walked_past_is_dropped()
    {
        Assert.Null(TelegramPlayer.Settle(T(2, 26), null, T(2, 40)));
        Assert.Null(TelegramPlayer.Settle(T(2, 26), T(2, 27), T(3, 10)));
    }

    [Fact]
    public void Position_at_the_very_end_is_not_treated_as_overrun()
        => Assert.Equal(T(3, 39), TelegramPlayer.Settle(T(3, 39), null, T(3, 39)));

    // 2013 = en dash, 2014 = em dash. Built from code points because sources here stay ascii, and an
    // editor that resolves \u escapes would put the raw character back into the file.
    private static string Dash(int cp) => ((char)cp).ToString();
    private static string Strip => "Katy Perry " + Dash(0x2013) + " Legendary Lovers";

    // the strip writes "performer <dash> title" as one line while smtc splits the two, so the label and
    // the pill's title are never equal - they are compared by containment
    [Fact]
    public void Strip_label_matches_the_smtc_title_it_contains()
        => Assert.True(TelegramPlayer.TitleMatches(Strip, "Legendary Lovers"));

    [Fact]
    public void Untagged_file_matches_itself_on_both_sides()
        => Assert.True(TelegramPlayer.TitleMatches("hs_01_clashreport_419cfc39.mp4", "hs_01_clashreport_419cfc39.mp4"));

    // the reported bug: a video plays while the music strip stays behind on the paused song, and the
    // song's duration got printed under the video
    [Fact]
    public void A_video_does_not_match_the_song_left_in_the_strip()
        => Assert.False(TelegramPlayer.TitleMatches(Strip, "hs_01_clashreport_208403808900182_419cfc39.mp4"));

    [Fact]
    public void Nothing_to_compare_is_not_a_match()
    {
        Assert.False(TelegramPlayer.TitleMatches(null, "Legendary Lovers"));
        Assert.False(TelegramPlayer.TitleMatches(Strip, null));
        Assert.False(TelegramPlayer.TitleMatches("   ", "Legendary Lovers"));
    }

    [Fact]
    public void Matching_ignores_case_dash_flavour_and_spacing()
    {
        Assert.True(TelegramPlayer.TitleMatches("Katy Perry " + Dash(0x2014) + " LEGENDARY  Lovers",
                                                "legendary lovers"));
        Assert.True(TelegramPlayer.TitleMatches("A_B_C track", "a-b-c track"));
    }
}
