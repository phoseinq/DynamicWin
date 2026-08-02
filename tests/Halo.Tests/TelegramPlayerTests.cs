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
}
