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

// Which calendar the date on the hourly banner is spoken in. Not a display preference: in Tehran the
// Gregorian date is a foreign fact, and in Berlin a Hijri one is.
internal enum CalendarKind { Gregorian, SolarHijri, SolarHijriAfghan, LunarHijri }

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
            if (Coords() is not { } c) return;
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

    /// <summary>
    /// Where to ask about the weather. Windows Location first, when the user has it switched on: it is the
    /// real answer, and a timezone is a very coarse one — every city in a zone got the same reading. The
    /// timezone city remains the fallback, and it is still what the banner is LABELLED with: the device gives
    /// coordinates and no name, and reverse geocoding would mean introducing another service for a word that
    /// is almost always the same word.
    ///
    /// The country still comes from geocoding that city even when the coordinates came from the device,
    /// because units and the calendar hang off it and a fix carries no country.
    /// </summary>
    private static (double lat, double lon)? Coords()
    {
        if (_coords is { } cached) return cached;
        if (DeviceLocation() is { } live)
        {
            _coords = live;
            FromDevice = true;
            if (PlaceCountry is null && Place is { Length: > 0 } named) _ = Geocode(named);
            return _coords;
        }
        if (Place is not { Length: > 0 } city) return null;
        _coords = Geocode(city);
        return _coords;
    }

    /// <summary>True when the reading is from the machine's own location rather than from its timezone.</summary>
    internal static volatile bool FromDevice;

    // A denied or switched-off location service is the normal case, not an error - so this is one probe, one
    // catch, and a silent fall back to the timezone. Blocking for a few seconds is fine: it runs on the
    // refresh timer, which already waits on an http call.
    private static (double lat, double lon)? DeviceLocation()
    {
        try
        {
            if (!LocationAllowed()) return null;
            var geo = new Windows.Devices.Geolocation.Geolocator
            {
                DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default,
                // a reading up to ten minutes old is the same weather, and asking for fresher spins the radios
                ReportInterval = 0,
            };
            var task = geo.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(8)).AsTask();
            if (!task.Wait(TimeSpan.FromSeconds(9))) return null;
            var p = task.Result?.Coordinate?.Point?.Position;
            return p is { } pos ? (pos.Latitude, pos.Longitude) : null;
        }
        catch { return null; }
    }

    // The system switch, read rather than assumed: if location is off or this app is denied, asking would
    // throw (or worse, prompt). "Allow" is the only value that means yes.
    private static bool LocationAllowed()
    {
        try
        {
            const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
            if (k?.GetValue("Value") as string is { } v)
                return string.Equals(v, "Allow", StringComparison.OrdinalIgnoreCase);
            using var m = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            return string.Equals(m?.GetValue("Value") as string, "Allow", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    // Where the Gregorian date is not the date anyone says out loud. Iran and Afghanistan run their civil
    // life on the Solar Hijri calendar, Saudi Arabia on the lunar one (Umm al-Qura). Anywhere else this is
    // noise, so the default is Gregorian and the list stays short: it is "which calendar is CIVIL here",
    // not "which countries are Muslim-majority" - Egypt, Turkey and Indonesia all run their diaries on
    // Gregorian and would be misinformed by a Hijri date, which is why guessing from language or from a
    // Muslim-majority list was rejected.
    internal static CalendarKind CalendarFor(string? cc, CalendarKind fallback)
        => cc is { Length: 2 } c
            ? c switch
            {
                "IR" => CalendarKind.SolarHijri,
                // same calendar as Iran's, different month names - Kabul says Hamal where Tehran says
                // Farvardin. This used to be left on Gregorian precisely because showing Iranian names to
                // an Afghan user is worse than showing none; the answer is the other table, not the omission.
                "AF" => CalendarKind.SolarHijriAfghan,
                "SA" => CalendarKind.LunarHijri,
                _ => CalendarKind.Gregorian,
            }
            : fallback;

    internal static bool Metric => MetricFor(PlaceCountry, RegionMetric);

    // the located country first, the machine's own region only as the fallback - a laptop carried abroad
    // keeps its Windows region long after it has stopped being where it is
    internal static CalendarKind Calendar => CalendarFor(PlaceCountry, RegionCalendar);

    private static bool RegionMetric
    {
        get { try { return RegionInfo.CurrentRegion.IsMetric; } catch { return true; } }
    }

    private static CalendarKind RegionCalendar
    {
        get
        {
            try { return CalendarFor(RegionInfo.CurrentRegion.TwoLetterISORegionName, CalendarKind.Gregorian); }
            catch { return CalendarKind.Gregorian; }
        }
    }

    private static readonly string[] JalaliMonths =
    {
        "Farvardin", "Ordibehesht", "Khordad", "Tir", "Mordad", "Shahrivar",
        "Mehr", "Aban", "Azar", "Dey", "Bahman", "Esfand",
    };

    private static readonly string[] AfghanMonths =
    {
        "Hamal", "Sawr", "Jawza", "Saratan", "Asad", "Sunbula",
        "Mizan", "Aqrab", "Qaws", "Jadi", "Dalw", "Hut",
    };

    private static readonly string[] HijriMonths =
    {
        "Muharram", "Safar", "Rabi I", "Rabi II", "Jumada I", "Jumada II",
        "Rajab", "Sha'ban", "Ramadan", "Shawwal", "Dhu al-Qi'dah", "Dhu al-Hijjah",
    };

    // PersianCalendar is in-box, so this is a conversion, not an approximation. Month names are
    // transliterated because the banner is English (docs/decisions.md) and the pill's font has no
    // reliable fallback for a mixed line here.
    internal static string? JalaliDate(DateTime now) => SolarDate(now, JalaliMonths);

    internal static string? AfghanDate(DateTime now) => SolarDate(now, AfghanMonths);

    private static string? SolarDate(DateTime now, string[] months)
    {
        try
        {
            var cal = new PersianCalendar();
            return cal.GetDayOfMonth(now) + " " + months[cal.GetMonth(now) - 1];
        }
        catch { return null; }
    }

    // UmAlQuraCalendar, not HijriCalendar: the plain one is a tabular arithmetic calendar and drifts a day
    // or two from the dates Saudi Arabia actually publishes, which is the whole point of showing it. It
    // also has a supported range (roughly 1900-2077), so a date outside it throws and we fall back to
    // Gregorian rather than showing a wrong one.
    internal static string? HijriDate(DateTime now)
    {
        try
        {
            var cal = new UmAlQuraCalendar();
            return cal.GetDayOfMonth(now) + " " + HijriMonths[cal.GetMonth(now) - 1];
        }
        catch { return null; }
    }

    internal static string? DateIn(CalendarKind kind, DateTime now) => kind switch
    {
        CalendarKind.SolarHijri => JalaliDate(now),
        CalendarKind.SolarHijriAfghan => AfghanDate(now),
        CalendarKind.LunarHijri => HijriDate(now),
        _ => null,
    };

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

    // The banner has three rows and the chime was only using two of them, so all four facts were queueing
    // up on one line: "Thursday 30 Jul · 8 Mordad · Tehran · 27°C clear". They fit the rows exactly.
    //
    //      TEHRAN                <- Label:    the place. Constant, so it belongs where the app name goes
    //      1:00 AM · 27°         <- Headline: the two numbers you came for
    //      Thursday, 8 Mordad    <- Detail:   the date, in the one calendar the place keeps
    //
    // and the sky is the badge. Nothing was dropped; one separator survives out of three.

    /// <summary>Where the reading is from, for the banner's label row. "Clock" when the zone names no city.</summary>
    internal static string Label => Place is { Length: > 0 } p ? p : "Clock";

    /// <summary>
    /// The time, with the temperature against it - both are numbers you read at a glance, and the title
    /// row is where the pill puts numbers. Just the time when there is no reading.
    /// </summary>
    internal static string Headline(DateTime now, Weather? w, bool metric)
    {
        var t = now.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return w is null ? t : t + " · " + Temp(w.TempC, metric);
    }

    /// <summary>
    /// The date. Pure, and InvariantCulture is not a detail: this machine is fa-IR, and the local culture
    /// would render the weekday in Persian inside a banner the rest of which is English.
    /// </summary>
    internal static string Detail(DateTime now, CalendarKind kind)
        => now.ToString("dddd", CultureInfo.InvariantCulture) + ", "
            + (DateIn(kind, now) is { Length: > 0 } d
                ? d : now.ToString("d MMM", CultureInfo.InvariantCulture));

    /// <summary>The same two lines, from the live snapshot.</summary>
    internal static string Headline(DateTime now) => Headline(now, Latest, Metric);

    internal static string Detail(DateTime now) => Detail(now, Calendar);
}
