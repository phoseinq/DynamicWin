using Halo.Widgets;

namespace Halo.Tests;

// The banner's text column is positioned for a two-line body. Halo's own alerts are the ones that carry
// no body at all -- "Screenshot captured", "Bad internet :/" -- and for months they drew their eyebrow and
// title at those same offsets, leaving the text visibly high against the icon beside it. Nothing here
// checks pixels; it checks that the two blocks agree on a centre, which is the property that was wrong.
public class NotifBannerLayoutTests
{
    private const float SummaryH = NotifBanner.SummaryH;

    // eyebrow top 22 + 14 tall, title top 41 + 26 tall, and the body block starts at 70
    private const float EyebrowTop = 22f, EyebrowH = 14f, TitleTop = 41f, TitleH = 26f;

    [Fact]
    public void A_banner_with_a_body_keeps_the_tuned_offsets()
        => Assert.Equal(0f, NotifBanner.TextShift(hasBody: true));

    [Fact]
    public void A_banner_with_no_body_centres_its_eyebrow_and_title_on_the_icon()
    {
        float shift = NotifBanner.TextShift(hasBody: false);
        float top = EyebrowTop + shift;
        float bottom = TitleTop + shift + TitleH;

        Assert.Equal(SummaryH / 2f, (top + bottom) / 2f, precision: 3);
    }

    [Fact]
    public void The_shift_only_ever_moves_text_down_and_never_off_the_banner()
    {
        float shift = NotifBanner.TextShift(hasBody: false);
        Assert.True(shift > 0f, "a body-less banner sat too high, so the correction must be downward");
        Assert.True(EyebrowTop + shift >= 0f);
        Assert.True(TitleTop + shift + TitleH <= SummaryH);
    }

    // the copy pill rides the title row, so it has to travel with it or a code-carrying banner with no
    // body would leave the button behind
    [Fact]
    public void The_copy_button_follows_the_title_row()
    {
        var withBody = new Halo.Notifications.NotifItem { Code = "482913", Body = "a body" };
        var without = new Halo.Notifications.NotifItem { Code = "482913" };

        float moved = NotifBanner.CopyRect(without, NotifBanner.W).Y
                    - NotifBanner.CopyRect(withBody, NotifBanner.W).Y;

        Assert.Equal(NotifBanner.TextShift(hasBody: false), moved, precision: 3);
    }

    [Fact]
    public void No_code_means_no_copy_button_to_hit_test()
        => Assert.True(NotifBanner.CopyRect(new Halo.Notifications.NotifItem(), NotifBanner.W).IsEmpty);

    // The grabber bar under a banner means "drag me, there is more". It used to appear for every message
    // that had a body at all, so a short one offered a handle that expanded into the same text and an empty
    // gap. The bar and the drag gesture both ask BodyOverflows now, so they cannot disagree.
    private static Halo.Notifications.NotifItem Body(string s) => new() { Body = s };

    [Fact]
    public void A_body_that_fits_the_two_summary_lines_offers_no_grabber()
        => Assert.False(NotifBanner.BodyOverflows(Body("on my way")));

    [Fact]
    public void A_body_too_long_for_two_lines_still_offers_one()
        => Assert.True(NotifBanner.BodyOverflows(Body(string.Join(" ",
            Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 12)))));

    [Fact]
    public void An_empty_body_offers_no_grabber()
        => Assert.False(NotifBanner.BodyOverflows(Body("")));

    // trailing newlines are common in mirrored toasts and are not something to read
    [Fact]
    public void Trailing_whitespace_is_not_more_to_read()
        => Assert.False(NotifBanner.BodyOverflows(Body("done   \r\n\r\n")));
}
