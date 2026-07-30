using System;
using System.Globalization;
using System.Threading;

namespace Halo.Shell;

internal static class Almanac
{

    internal sealed record Weather(int TempC, int Code, bool Day = true);

    internal static volatile Weather? Latest;

    internal static string? Place { get; } = CityFromTimeZone();

    internal static string? CityFromTimeZone()
    {
        try
        {
            var id = TimeZoneInfo.Local.Id;

            if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) || string.IsNullOrEmpty(iana))
                iana = id;
            return CityFromIana(iana);
        }
        catch { return null; }
    }

        internal static string? CityFromIana(string iana)
    {
        int slash = iana.LastIndexOf('/');
        var city = (slash >= 0 ? iana[(slash + 1)..] : iana).Replace('_', ' ').Trim();

        return city.Length == 0 || city.Contains("GMT", StringComparison.OrdinalIgnoreCase)
            || city.Equals("UTC", StringComparison.OrdinalIgnoreCase) ? null : city;
    }

    private static Timer? _timer;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static (double lat, double lon)? _coords;

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
        catch { }
    }

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

        internal static volatile bool FromDevice;

    private static (double lat, double lon)? DeviceLocation()
    {
        try
        {
            if (!LocationAllowed()) return null;
            var geo = new Windows.Devices.Geolocation.Geolocator
            {
                DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default,

                ReportInterval = 0,
            };
            var task = geo.GetGeopositionAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(8)).AsTask();
            if (!task.Wait(TimeSpan.FromSeconds(9))) return null;
            var p = task.Result?.Coordinate?.Point?.Position;
            return p is { } pos ? (pos.Latitude, pos.Longitude) : null;
        }
        catch { return null; }
    }

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

    internal static volatile string? PlaceCountry;

    internal static bool MetricFor(string? cc, bool fallback)
        => cc is { Length: 2 } c ? c is not ("US" or "LR" or "MM") : fallback;

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

    internal static string? JalaliDate(DateTime now)
    {
        try
        {
            var cal = new PersianCalendar();
            return cal.GetDayOfMonth(now) + " " + JalaliMonths[cal.GetMonth(now) - 1];
        }
        catch { return null; }
    }

        internal static (int glyph, int hue) SkyBadge(int code, bool day) => code switch
    {

        0 or 1 => day ? (0xE706, 30) : (0xE708, 232),
        2 => day ? (0xE706, 26) : (0xE708, 226),
        45 or 48 => (0xE753, 196),
        51 or 53 or 55 or 56 or 57 => (0xE753, 208),
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => (0xE753, 220),
        71 or 73 or 75 or 77 or 85 or 86 => (0xEA38, 188),
        95 or 96 or 99 => (0xE753, 280),
        _ => (0xE753, 210),
    };

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

    private static string Temp(int c, bool metric)
        => (metric ? c : (int)Math.Round(c * 9 / 5.0 + 32)) + "°";

        internal static string Label => Place is { Length: > 0 } p ? p : "Clock";

        internal static string Headline(DateTime now, Weather? w, bool metric)
    {
        var t = now.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return w is null ? t : t + " · " + Temp(w.TempC, metric);
    }

        internal static string Detail(DateTime now, bool jalali)
        => now.ToString("dddd", CultureInfo.InvariantCulture) + ", "
            + (jalali && JalaliDate(now) is { Length: > 0 } j
                ? j : now.ToString("d MMM", CultureInfo.InvariantCulture));

        internal static string Headline(DateTime now) => Headline(now, Latest, Metric);

    internal static string Detail(DateTime now) => Detail(now, SolarHijri);
}
