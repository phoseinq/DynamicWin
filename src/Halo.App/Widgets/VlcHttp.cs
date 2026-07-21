using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Halo.Widgets;

// Classic VLC (3.x) has no SMTC AND exposes no rate to read via hotkeys — so the speed always desynced and
// the label lied. This drives VLC's Lua HTTP interface instead: it SETS the exact rate and READS BACK the
// real rate + play/pause state, so the pill always matches VLC. We enable the interface by writing vlcrc
// (extraintf=http + a localhost-only password); VLC only reads vlcrc at startup, so it takes effect the next
// time VLC launches — until then callers fall back to the old hotkey path so the buttons still do something.
internal static class VlcHttp
{
    public static volatile bool Online;        // http reachable → Rate/Playing are live and commands go over http
    public static double Rate = 1.0;           // last read playback rate (1.0 = normal)
    public static volatile bool Playing = true; // last read transport state
    public static volatile bool SubsOn = true;  // subtitle on/off — a LOCAL guess (VLC exposes no live spu track)
    private static string _lastPlid = "";       // playlist item id — detect a new media item to re-seed SubsOn

    private const int Port = 8080;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private static volatile bool _configured;  // vlcrc has a password we know → commands/polls are armed

    private static string RcPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");

    // ── config ── make sure vlcrc enables the http interface. Idempotent + cheap (one read when already set).
    // Call ONLY when VLC is not running (startup / just-closed): a running VLC rewrites vlcrc on exit and
    // would clobber our keys. All four keys already exist (commented) in a normal vlcrc, so SetKey replaces
    // them in place and they stay section-correct (extraintf/http-host/http-port in [core], password in [lua]).
    public static void EnsureConfigured()
    {
        try
        {
            string path = RcPath;
            if (!File.Exists(path)) return; // no VLC profile yet — nothing to enable (created on first VLC run)
            string text = File.ReadAllText(path);
            string? pw = ReadKey(text, "http-password");
            bool httpOn = (ReadKey(text, "extraintf") ?? "").Split(':').Contains("http");
            if (httpOn && !string.IsNullOrEmpty(pw)) { Arm(pw!); return; } // already configured

            pw = string.IsNullOrEmpty(pw) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) : pw;
            text = SetKey(text, "extraintf", "http");
            text = SetKey(text, "http-host", "127.0.0.1");
            text = SetKey(text, "http-port", Port.ToString(CultureInfo.InvariantCulture));
            text = SetKey(text, "http-password", pw);
            File.WriteAllText(path, text);
            Arm(pw);
        }
        catch { }
    }

    private static void Arm(string pw)
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pw))); // VLC http auth = empty user + password
        _configured = true;
    }

    // read an uncommented `key=value` (a leading '#' means commented/default → treated as unset); null if absent
    internal static string? ReadKey(string vlcrc, string key)
    {
        var m = Regex.Match(vlcrc, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*(.*)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // set `key=value`, replacing the first commented-or-live line for that key; append only if it's missing
    // entirely (ponytail: a normal vlcrc always has the key commented, so the append fallback — which can't
    // know the right [section] — never fires in practice).
    internal static string SetKey(string vlcrc, string key, string value)
    {
        var rx = new Regex($@"(?m)^\s*#?\s*{Regex.Escape(key)}\s*=.*$");
        return rx.IsMatch(vlcrc)
            ? rx.Replace(vlcrc, $"{key}={value}", 1)
            : vlcrc.TrimEnd('\r', '\n') + $"\n{key}={value}\n";
    }

    // ── live state ── called on the monitor thread while VLC is present
    public static void Poll()
    {
        if (!_configured) { Online = false; return; }
        try
        {
            string xml = Get("/requests/status.xml");
            var (rate, playing) = ParseStatus(xml);
            Rate = rate; Playing = playing;
            // VLC exposes no live spu-track field, so SubsOn can't be read — re-seed it to VLC's default
            // each time a NEW media item starts (a soft-sub track auto-shows), then follow our own toggles.
            string plid = Regex.Match(xml, @"<currentplid>(-?\d+)</currentplid>").Groups[1].Value;
            if (plid != _lastPlid) { _lastPlid = plid; SubsOn = xml.Contains(">Subtitle<"); }
            Online = true;
        }
        catch { Online = false; }
    }

    internal static (double rate, bool playing) ParseStatus(string xml)
    {
        double rate = 1.0; bool playing = true;
        var mr = Regex.Match(xml, @"<rate>([\d.]+)</rate>");
        if (mr.Success) double.TryParse(mr.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
        var ms = Regex.Match(xml, @"<state>(\w+)</state>");
        if (ms.Success) playing = ms.Groups[1].Value == "playing";
        return (rate, playing);
    }

    // smallest preset strictly above the current rate, wrapping to the first — steps up from wherever VLC
    // actually is (even a rate the user set inside VLC), so the cycle never desyncs
    internal static double NextPreset(double current, double[] presets)
    {
        foreach (var p in presets) if (p > current + 0.01) return p;
        return presets[0];
    }

    public static void SetRate(double r) { Rate = r; Send($"?command=rate&val={r.ToString(CultureInfo.InvariantCulture)}"); }
    public static void TogglePlay() { Playing = !Playing; Send("?command=pl_pause"); }
    public static void Seek(int seconds) { Send($"?command=seek&val={(seconds >= 0 ? "+" : "")}{seconds}S"); }
    // cycle subtitle track (Off → 1 → 2 → Off), same action as the 'V' hotkey but over http (no focus steal).
    // ponytail: SubsOn is a local guess (no readable spu track) — exact for one subtitle track, approximate
    // for a file with several.
    public static void CycleSubtitle() { SubsOn = !SubsOn; Send("?command=key&val=subtitle-track"); }

    // fire-and-forget on a pool thread so a click never blocks the UI; local state was already updated
    // optimistically, and the next Poll reconciles it with VLC's truth.
    private static void Send(string query)
    {
        if (!_configured) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Get("/requests/status.xml" + query); Online = true; } catch { Online = false; }
        });
    }

    private static string Get(string path)
        => Http.GetStringAsync($"http://127.0.0.1:{Port}{path}").GetAwaiter().GetResult();
}
