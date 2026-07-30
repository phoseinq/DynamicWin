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

internal static class VlcHttp
{
    public static volatile bool Online;
    public static double Rate = 1.0;

    public static int Time;
    public static int Length;
    public static volatile string? Resolution;
    private static long _seekSentAt;
    private static int _seekTarget = -1;
    public static volatile bool Playing = true;
    public static volatile bool SubsOn = true;
    private static string _lastPlid = "";

    private const int Port = 8080;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(800) };
    private static volatile bool _configured;

    private static string RcPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vlc", "vlcrc");

    public static void EnsureConfigured()
    {
        try
        {
            string path = RcPath;
            if (!File.Exists(path)) return;
            string text = File.ReadAllText(path);
            string? pw = ReadKey(text, "http-password");
            bool httpOn = (ReadKey(text, "extraintf") ?? "").Split(':').Contains("http");
            if (httpOn && !string.IsNullOrEmpty(pw)) { Arm(pw!); return; }

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
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pw)));
        _configured = true;
    }

    internal static string? ReadKey(string vlcrc, string key)
    {
        var m = Regex.Match(vlcrc, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*(.*)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    internal static string SetKey(string vlcrc, string key, string value)
    {
        var rx = new Regex($@"(?m)^\s*#?\s*{Regex.Escape(key)}\s*=.*$");
        return rx.IsMatch(vlcrc)
            ? rx.Replace(vlcrc, $"{key}={value}", 1)
            : vlcrc.TrimEnd('\r', '\n') + $"\n{key}={value}\n";
    }

    public static void Poll()
    {
        if (!_configured) { Online = false; return; }
        try
        {
            string xml = Get("/requests/status.xml");
            var (rate, playing) = ParseStatus(xml);
            Rate = rate; Playing = playing;
            var (time, length) = ParseTime(xml);
            Length = length;

            bool settled = _seekTarget < 0 || Math.Abs(time - _seekTarget) <= 2
                || Environment.TickCount64 - _seekSentAt > 1500;
            if (settled) { Time = time; _seekTarget = -1; }
            Resolution = ParseResolution(xml);

            string plid = Regex.Match(xml, @"<currentplid>(-?\d+)</currentplid>").Groups[1].Value;
            if (plid != _lastPlid) { _lastPlid = plid; SubsOn = xml.Contains(">Subtitle<"); }
            Online = true;
        }
        catch { Online = false; }
    }

    internal static (int time, int length) ParseTime(string xml)
    {
        int time = -1, length = 0;
        var mt = Regex.Match(xml, @"<time>(-?\d+)</time>");
        if (mt.Success) int.TryParse(mt.Groups[1].Value, out time);
        var ml = Regex.Match(xml, @"<length>(-?\d+)</length>");
        if (ml.Success) int.TryParse(ml.Groups[1].Value, out length);
        if (length < 0) length = 0;
        return (time, length);
    }

    internal static string? ParseResolution(string xml)
    {
        var m = Regex.Match(xml, @"name=.Video_resolution.>\s*(\d+x\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(xml, @"name=.Resolution.>\s*(\d+x\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
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

    internal static double NextPreset(double current, double[] presets)
    {
        foreach (var p in presets) if (p > current + 0.01) return p;
        return presets[0];
    }

    public static void SetRate(double r) { Rate = r; Send($"?command=rate&val={r.ToString(CultureInfo.InvariantCulture)}"); }
    public static void TogglePlay() { Playing = !Playing; Send("?command=pl_pause"); }
    public static void Seek(int seconds) { Send($"?command=seek&val={(seconds >= 0 ? "+" : "")}{seconds}S"); }

        public static void SeekTo(float frac)
    {
        int len = Length;
        if (len <= 0) return;
        frac = Math.Clamp(frac, 0f, 1f);
        int target = (int)(frac * len);
        Time = target;
        _seekTarget = target;
        _seekSentAt = Environment.TickCount64;
        Send($"?command=seek&val={(int)(frac * 100)}%");
    }

    public static void CycleSubtitle() { SubsOn = !SubsOn; Send("?command=key&val=subtitle-track"); }

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
