using System;
using System.Linq;
using Halo.Shell;
using Xunit;

namespace Halo.Tests;

// The hourly banner's second line. It is assembled from parts that are each separately unknowable - no
// weather because the fetch failed, no place because the machine sits on UTC - so what needs pinning is
// that a missing part is simply absent and never filled in with a guess.
public class AlmanacTests
{
    private static readonly DateTime Afternoon = new(2026, 7, 30, 15, 0, 0);   // a Thursday

    // Four facts used to queue up on one line - "Thursday 30 Jul · 8 Mordad · Tehran · 34°C clear" - while
    // the banner had a third row sitting empty. One fact per row now, and exactly one separator survives.
    [Fact]
    public void EachRowCarriesOneThing()
    {
        Assert.Equal("1:00 PM · 34°", Almanac.Headline(Afternoon.Date.AddHours(13), new Almanac.Weather(34, 0), metric: true));
        Assert.Equal("Thursday, 8 Mordad", Almanac.Detail(Afternoon, jalali: true));
        Assert.Equal("Thursday, 30 Jul", Almanac.Detail(Afternoon, jalali: false));
    }

    [Fact]
    public void NothingIsSaidTwiceAndTheSkyIsNotSaidAtAll()
    {
        var whole = Almanac.Headline(Afternoon, new Almanac.Weather(34, 0), metric: true)
            + " " + Almanac.Detail(Afternoon, jalali: true);
        Assert.Equal(1, whole.Count(c => c == '·'));
        Assert.DoesNotContain("Jul", whole);      // one calendar, the one the place keeps
        Assert.DoesNotContain("clear", whole);    // the sky is the badge
    }

    [Fact]
    public void WithNoReadingTheTitleIsJustTheClock()
        => Assert.Equal("3:00 PM", Almanac.Headline(Afternoon, null, metric: true));

    [Fact]
    public void AnImperialMachineGetsFahrenheit()
        => Assert.Contains("93°", Almanac.Headline(Afternoon, new Almanac.Weather(34, 0), metric: false));

    // the place is constant, so it goes where the app name goes rather than costing a field every hour
    [Fact]
    public void TheLabelIsThePlaceOrTheFallback()
        => Assert.False(string.IsNullOrWhiteSpace(Almanac.Label));

    [Fact]
    public void TheSolarHijriDateIsAConversionNotAnEstimate()
    {
        Assert.Equal("8 Mordad", Almanac.JalaliDate(Afternoon));
        Assert.Contains("8 Mordad", Almanac.Detail(Afternoon, jalali: true));
    }

    // the sky is a glyph and a hue now. What must hold is that every code lands on a glyph that exists in
    // Segoe Fluent Icons (--render-badges is the eyeball for that) and that night is not drawn as a sun.
    [Theory]
    [InlineData(0, true, 0xE706)]    // clear day: sun
    [InlineData(0, false, 0xE708)]   // clear night: moon
    [InlineData(2, false, 0xE708)]
    [InlineData(3, true, 0xE753)]    // overcast: cloud
    [InlineData(63, true, 0xE753)]   // rain: cloud, hued blue
    [InlineData(73, true, 0xEA38)]   // snow: the flake
    [InlineData(95, true, 0xE753)]   // storm
    [InlineData(4242, true, 0xE753)] // unknown code: a plain cloud, never a sun
    public void TheSkyBadgeUsesOnlyGlyphsThatExist(int code, bool day, int glyph)
        => Assert.Equal(glyph, Almanac.SkyBadge(code, day).glyph);

    [Fact]
    public void NightIsNeverAWarmHue()
    {
        var (_, dayHue) = Almanac.SkyBadge(0, day: true);
        var (_, nightHue) = Almanac.SkyBadge(0, day: false);
        Assert.InRange(dayHue, 20, 60);      // amber
        Assert.InRange(nightHue, 200, 260);  // indigo
    }

    [Theory]
    [InlineData("Asia/Tehran", "Tehran")]
    [InlineData("Europe/London", "London")]
    [InlineData("America/Argentina/Buenos_Aires", "Buenos Aires")]
    [InlineData("Asia/Ho_Chi_Minh", "Ho Chi Minh")]
    public void TheCityComesOutOfTheZoneId(string iana, string city)
        => Assert.Equal(city, Almanac.CityFromIana(iana));

    // these name an offset, not a place. Saying "GMT+3" where a city goes would look like a bug.
    [Theory]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+3")]
    [InlineData("Etc/UTC")]
    public void AnOffsetIsNotAPlace(string iana) => Assert.Null(Almanac.CityFromIana(iana));

    // Reported by the first live probe: this machine's Windows region is US while its timezone is Iran, so
    // the banner said "Tehran 81°F". Units and the calendar follow the place being described, and fall back
    // to the region only when there is no place yet.
    [Theory]
    [InlineData("IR", true)]
    [InlineData("GB", true)]
    [InlineData("US", false)]
    [InlineData("LR", false)]
    public void UnitsFollowThePlaceAndNotTheMachine(string cc, bool metric)
        => Assert.Equal(metric, Almanac.MetricFor(cc, fallback: !metric));

    [Fact]
    public void WithNoPlaceYetTheMachinesOwnSettingStands()
    {
        Assert.True(Almanac.MetricFor(null, fallback: true));
        Assert.False(Almanac.MetricFor(null, fallback: false));
        Assert.True(Almanac.SolarHijriFor(null, fallback: true));
    }

    [Theory]
    [InlineData("IR", true)]
    [InlineData("US", false)]
    [InlineData("AF", false)]   // also solar, but with different month names - not worth being wrong about
    public void TheSolarCalendarIsForIran(string cc, bool jalali)
        => Assert.Equal(jalali, Almanac.SolarHijriFor(cc, fallback: !jalali));

    [Fact]
    public void TheMachinesOwnZoneResolvesToSomethingOrToNothing()
    {
        // whatever this box is set to, the probe must not throw and must not leak the id's punctuation
        var city = Almanac.CityFromTimeZone();
        if (city is null) return;
        Assert.DoesNotContain("/", city);
        Assert.DoesNotContain("_", city);
    }
}
