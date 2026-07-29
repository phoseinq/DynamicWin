using System;
using System.Globalization;
using System.Threading;

namespace Halo.Shell;

// The hourly chime said the time and nothing else, which is the one thing the tray clock already tells
// you. This is the rest of the glance: what day it is, where the machine thinks it is, and the weather
// there.
//
// WHERE comes from the machine's own timezone, not from an IP lookup. IpCountry already knows the exit
// country, but that is where the VPN surfaces, not where you are - and the timezone is the one location
// fact on the box that the user set themselves. Windows id -> IANA -> the segment after the last slash
// ("Iran Standard Time" -> "Asia/Tehran" -> "Tehran"). Approximate by construction, which is exactly why
// the banner names a city and never coordinates.
//
// WEATHER is Open-Meteo, keyless: once to turn that city into coordinates, then the reading itself. It
// runs on a timer and never on the chime's own path - a banner that waited on the network would arrive
// late or not at all - so the chime shows whatever the last refresh left behind and simply says nothing
// about weather when that is nothing. Every figure here is measured or converted; none is invented.
internal static class Almanac
{
    // Day is not a guess from the hour: Open-Meteo answers is_day for the point it just described, so the
    // badge can show a moon at nine in the evening in July and still be right in January.
    internal sealed record Weather(int TempC, int Code, bool Day = true);

    // reference type so the field itself can be volatile: written on a timer thread, read on the UI one
    internal static volatile Weather? Latest;

    // one probe at type-init. A timezone change mid-process is a reboot-shaped event on Windows, and
    // being one city stale until then costs nothing.
    internal static string? Place { get; } = CityFromTimeZone();

    internal static string? CityFromTimeZone()
    {
        try
        {
            var id = TimeZoneInfo.Local.Id;
            // already IANA (non-Windows host), or an id ICU has no mapping for: fall back to it verbatim
            if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) || string.IsNullOrEmpty(iana))
                iana = id;
            return CityFromIana(iana);
        }
        catch { return null; }
    }

    /// <summary>
    /// The city out of an IANA zone id: the segment after the last slash, underscores back to spaces.
    /// Split out from the timezone probe so it can be tested - the machine running the test has exactly
    /// one timezone, and the interesting cases are the other ones.
    /// </summary>
    internal static string? CityFromIana(string iana)
    {
        int slash = iana.LastIndexOf('/');
        var city = (slash >= 0 ? iana[(slash + 1)..] : iana).Replace('_', ' ').Trim();
        // "UTC", "Etc/GMT+3" and friends name an offset, not a place - there is nothing to say
        return city.Length == 0 || city.Contains("GMT", StringComparison.OrdinalIgnoreCase)
            || city.Equals("UTC", StringComparison.OrdinalIgnoreCase) ? null : city;
    }

    private static Timer? _timer;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static (double lat, double lon)? _coords;

    /// <summary>
    /// Arm the refresh. Armed from the alert loop rather than the constructor so it costs nothing until
    /// the pill is actually running: 20s in, so it is not competing with startup, then every half hour -
    /// the chime is hourly, and a reading thirty minutes old is still the weather.
    /// </summary>
    public static void Poke() => _timer ??= new Timer(_ => Refresh(), null, 20_000, 1_800_000);

    private static void Refresh()
    {
        try
        {
            if (Place is not { Length: > 0 } city) return;
            _coords ??= Geocode(city);
            if (_coords is not { } c) return;
            var url = "https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code,is_day"
                + "&latitude=" + c.lat.ToString("0.####", CultureInfo.InvariantCulture)
                + "&longitude=" + c.lon.ToString("0.####", CultureInfo.InvariantCulture);
            using var doc = System.Text.Json.JsonDocument.Parse(Http.GetStringAsync(url).Result);
            var cur = doc.RootElement.GetProperty("current");
            Latest = new Weather(
                (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble()),
                cur.GetProperty("weather_code").GetInt32(),
                !cur.TryGetProperty("is_day", out var day) || day.GetInt32() != 0);
        }
        catch { }   // a failed probe is normal here: the banner just carries no weather
    }

    // the city name is all we have, so the geocoder turns it into a point. Cached for the process - the
    // answer for "Tehran" is not going to move.
    private static (double lat, double lon)? Geocode(string city)
    {
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name="
                + Uri.EscapeDataString(city);
            using var doc = System.Text.Json.JsonDocument.Parse(Http.GetStringAsync(url).Result);
            if (!doc.RootElement.TryGetProperty("results", out var r) || r.GetArrayLength() == 0) return null;
            if (r[0].TryGetProperty("country_code", out var cc) && cc.GetString() is { Length: 2 } code)
                PlaceCountry = code.ToUpperInvariant();
            return (r[0].GetProperty("latitude").GetDouble(), r[0].GetProperty("longitude").GetDouble());
        }
        catch { return null; }
    }

    // The country of the place being reported, straight off the geocoder that resolved it. Units and the
    // calendar follow the PLACE and not the Windows region setting, because those two really do disagree:
    // this machine's region is US while its timezone is Iran, and the first probe of this duly produced
    // "Tehran 81F" - describing Tehran in somebody else's units. The region stays as the fallback for
    // before the first geocode lands, or when the zone names no city at all.
    internal static volatile string? PlaceCountry;

    // metric for weather is everyone except the US, Liberia and Myanmar
    internal static bool MetricFor(string? cc, bool fallback)
        => cc is { Length: 2 } c ? c is not ("US" or "LR" or "MM") : fallback;

    // Iran runs on the Solar Hijri calendar, so there the Gregorian date is not the date anyone says out
    // loud. Only for a machine that is actually there - it would be noise anywhere else.
    internal static bool SolarHijriFor(string? cc, bool fallback)
        => cc is { Length: 2 } c ? c == "IR" : fallback;

    internal static bool Metric => MetricFor(PlaceCountry, RegionMetric);

    internal static bool SolarHijri => SolarHijriFor(PlaceCountry, RegionIsIran);

    private static bool RegionMetric
    {
        get { try { return RegionInfo.CurrentRegion.IsMetric; } catch { return true; } }
    }

    private static bool RegionIsIran
    {
        get { try { return RegionInfo.CurrentRegion.TwoLetterISORegionName == "IR"; } catch { return false; } }
    }

    private static readonly string[] JalaliMonths =
    {
        "Farvardin", "Ordibehesht", "Khordad", "Tir", "Mordad", "Shahrivar",
        "Mehr", "Aban", "Azar", "Dey", "Bahman", "Esfand",
    };

    // PersianCalendar is in-box, so this is a conversion, not an approximation. Month names are
    // transliterated because the banner is English (docs/decisions.md) and the pill's font has no
    // reliable fallback for a mixed line here.
    internal static string? JalaliDate(DateTime now)
    {
        try
        {
            var cal = new PersianCalendar();
            return cal.GetDayOfMonth(now) + " " + JalaliMonths[cal.GetMonth(now) - 1];
        }
        catch { return null; }
    }

    /// <summary>
    /// The sky as a badge instead of as words: which Fluent glyph, and what hue the tile behind it takes.
    /// The banner already carries an icon, and a picture of a sun says "clear" without spending any of the
    /// line on it - three separators of text was the clutter that got this rewritten.
    ///
    /// Only glyphs verified present in Segoe Fluent Icons are used (sun, moon, cloud, and the six-point
    /// asterisk that reads as a flake), so what the shape cannot distinguish, the hue does: rain is a blue
    /// cloud, overcast a grey one, a storm a violet one. The tile colour is not decoration either - it is
    /// what the banner's glow samples.
    /// </summary>
    internal static (int glyph, int hue) SkyBadge(int code, bool day) => code switch
    {
        // the tile gradient runs hue → hue+24, so a "sunny" 44 ended up gold→GREEN and read as acidic
        // beside the others; 30 lands the pair on gold→yellow, which is what a sun is
        0 or 1 => day ? (0xE706, 30) : (0xE708, 232),                     // Brightness / Moon
        2 => day ? (0xE706, 26) : (0xE708, 226),                          // mostly clear: still the sun
        45 or 48 => (0xE753, 196),                                        // fog: pale cloud
        51 or 53 or 55 or 56 or 57 => (0xE753, 208),                       // drizzle
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => (0xE753, 220),      // rain: a blue cloud
        71 or 73 or 75 or 77 or 85 or 86 => (0xEA38, 188),                  // snow: the flake, pale cyan
        95 or 96 or 99 => (0xE753, 280),                                   // storm: violet
        _ => (0xE753, 210),                                                // overcast, and anything unknown
    };

    // WMO weather codes, collapsed to the handful of words that change what you would do about it. Not on
    // the banner any more - the badge says this now - but the probe still prints it, and it is the only
    // readable check that a code maps where it should.
    internal static string Sky(int code) => code switch
    {
        0 => "clear",
        1 or 2 => "fair",
        3 => "overcast",
        45 or 48 => "fog",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 => "rain",
        71 or 73 or 75 or 77 => "snow",
        80 or 81 or 82 => "showers",
        85 or 86 => "snow showers",
        95 or 96 or 99 => "storm",
        _ => "",
    };

    // no unit letter: the place is named right beside it, and nobody reads "Tehran 27°" as Fahrenheit. The
    // letter was pure width in a line that had too much of everything.
    private static string Temp(int c, bool metric)
        => (metric ? c : (int)Math.Round(c * 9 / 5.0 + 32)) + "°";

    /// <summary>
    /// The chime's second line, from whatever is known. Pure, so the shape can be pinned by a test at
    /// every level of ignorance: no weather, no place, neither.
    ///
    /// It was "Thursday 30 Jul · 8 Mordad · Tehran · 27°C clear" and that was three separators of noise
    /// saying the same day twice. Now: the weekday, the date in the ONE calendar the place actually uses,
    /// and the place with its temperature - "Thursday, 8 Mordad · Tehran 27°". The sky is the badge.
    ///
    /// InvariantCulture is not a detail: this machine is fa-IR, and the local culture would render the
    /// weekday in Persian inside a banner the rest of which is English.
    /// </summary>
    internal static string Detail(DateTime now, string? place, Weather? w, bool metric, bool jalali)
    {
        var s = now.ToString("dddd", CultureInfo.InvariantCulture) + ", "
            + (jalali && JalaliDate(now) is { Length: > 0 } j
                ? j : now.ToString("d MMM", CultureInfo.InvariantCulture));
        if (place is { Length: > 0 }) s += " · " + place;
        // the temperature belongs to the place, so it sits against it rather than behind another separator
        if (w is not null) s += (place is { Length: > 0 } ? " " : " · ") + Temp(w.TempC, metric);
        return s;
    }

    /// <summary>The same line, from the live snapshot.</summary>
    internal static string Detail(DateTime now) => Detail(now, Place, Latest, Metric, SolarHijri);
}
