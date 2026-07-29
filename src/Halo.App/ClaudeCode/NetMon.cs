using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Halo.ClaudeCode;

internal static class NetMon
{
    public const int Lost = -1, Empty = -2;
    private static readonly int[] _net = CreateBuf(), _api = CreateBuf();
    private static int _idx;
    private static DateTime _until = DateTime.MinValue;
    private static Thread? _thread;

    public static int Version;

    public static volatile bool ApiDown, NetDown, Slow;
    private const int SlowMs = 1500;
    private static int _slowStreak;

    private static int[] CreateBuf()
    {
        var b = new int[24];
        Array.Fill(b, Empty);
        return b;
    }

    static NetMon() => EnsureThread();

    public static void Poke()
    {
        IpCountry.Poke();
        _until = DateTime.UtcNow.AddSeconds(8);
        EnsureThread();
    }

    private static void EnsureThread()
    {
        if (_thread == null)
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }
    }

    public static (int[] net, int[] api) Snapshot()
    {
        lock (_net)
        {
            var n = new int[_net.Length];
            var a = new int[_api.Length];
            for (int i = 0; i < _net.Length; i++)
            {
                n[i] = _net[(_idx + i) % _net.Length];
                a[i] = _api[(_idx + i) % _api.Length];
            }
            return (n, a);
        }
    }

    private static void Loop()
    {
        var lastBg = DateTime.MinValue;
        while (true)
        {

            if (DateTime.UtcNow - lastBg > TimeSpan.FromSeconds(10))
            {
                lastBg = DateTime.UtcNow;
                int apiMs = HttpLatency(HttpApi, "https://api.anthropic.com/v1/messages", fresh: true);
                bool apiDown = apiMs == Lost;
                int netMs = HttpLatency(HttpNet, "https://www.google.com/generate_204", fresh: true);
                bool netDown = apiDown && netMs == Lost;
                SetHealth(apiDown, netDown);
                bool bad = netMs == Lost || netMs > SlowMs;
                _slowStreak = bad ? _slowStreak + 1 : 0;
                SetSlow(_slowStreak >= 2);

                RecordSample(netMs, apiMs);
            }
            if (DateTime.UtcNow < _until)
            {

                int apiMs = Lost;
                var apiTask = new Thread(() => apiMs = HttpLatency(HttpApi, "https://api.anthropic.com/v1/messages")) { IsBackground = true };
                apiTask.Start();
                int netMs = HttpLatency(HttpNet, "https://www.google.com/generate_204");
                apiTask.Join(2600);

                RecordSample(netMs, apiMs);
                Thread.Sleep(700);
            }
            else Thread.Sleep(300);
        }
    }

    private static void RecordSample(int netMs, int apiMs)
    {
        lock (_net) { _net[_idx] = netMs; _api[_idx] = apiMs; _idx = (_idx + 1) % _net.Length; }
        Interlocked.Increment(ref Version);
    }

    private static void SetHealth(bool apiDown, bool netDown)
    {
        if (apiDown == ApiDown && netDown == NetDown) return;
        ApiDown = apiDown;
        NetDown = netDown;
        IpCountry.Invalidate();
        Interlocked.Increment(ref Version);
    }

    private static void SetSlow(bool slow)
    {
        if (slow == Slow) return;
        Slow = slow;
        Interlocked.Increment(ref Version);
    }

    private static readonly System.Net.Http.HttpClient HttpApi = new(ProxiedHandler())
    { Timeout = TimeSpan.FromSeconds(2.5) };
    private static readonly System.Net.Http.HttpClient HttpNet = new(
        new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(2.5) };

    internal static string? ProxyUrl =>
        Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
        ?? Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.User);

    private static System.Net.Http.SocketsHttpHandler ProxiedHandler()
    {
        var h = new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var proxy = ProxyUrl;
        if (!string.IsNullOrEmpty(proxy))
            try { h.Proxy = new System.Net.WebProxy(proxy); h.UseProxy = true; } catch { h.UseProxy = false; }
        else
            h.UseProxy = false;
        return h;
    }

    private static int HttpLatency(System.Net.Http.HttpClient http, string url, bool fresh = false)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (fresh) req.Headers.ConnectionClose = true;
            using var resp = http.Send(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            int sc = (int)resp.StatusCode;
            return IsDownStatus(sc) ? Lost : (int)sw.ElapsedMilliseconds;
        }
        catch { return Lost; }
    }

    internal static bool IsDownStatus(int statusCode) =>
        statusCode >= 500 || statusCode == 403 || statusCode == 407 || statusCode == 429;
}

internal static class IpRep
{
    public static volatile string? ForIp;
    public static volatile string? Verdict;
    public static volatile string? Abuse;
    public static volatile int Sev;

    public static volatile bool Tor, Abuser, Bogon, Vpn, Proxy, Datacenter;

    private static int _busy;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static void Want(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return;
        if (string.Equals(ForIp, ip, StringComparison.Ordinal)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Fetch(ip); } catch { } finally { Volatile.Write(ref _busy, 0); }
        });
    }

    internal static string? AbuseLabel(string? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        int open = raw.IndexOf('('), close = open < 0 ? -1 : raw.IndexOf(')', open);
        if (open < 0 || close <= open) return null;
        string s = raw.Substring(open + 1, close - open - 1).Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    internal static (string verdict, int sev) Classify(bool tor, bool abuser, bool bogon, bool vpn,
        bool proxy, bool datacenter, bool mobile, string? abuse)
    {
        var (verdict, sev) =
              tor ? ("flagged: tor", 3)
            : abuser ? ("flagged: abuse", 3)
            : bogon ? ("flagged: bogon", 3)
            : vpn ? ("vpn, recognised", 2)
            : proxy ? ("proxy, recognised", 2)
            : datacenter ? ("datacenter", 1)
            : mobile ? ("mobile", 0)
            : ("residential", 0);

        if (sev < 2 && abuse is "high" or "very high") sev = 2;
        return (verdict, sev);
    }

    internal static int Score(bool tor, bool abuser, bool bogon, bool vpn, bool proxy, bool datacenter,
                              string? abuse, bool split, bool dnsLeak)
    {
        int s = 100;
        if (tor) s -= 55;
        if (abuser) s -= 45;
        if (bogon) s -= 45;
        if (vpn || proxy) s -= 22;
        if (datacenter) s -= 14;
        if (abuse == "very high") s -= 22;
        else if (abuse == "high") s -= 14;
        if (split) s -= 12;
        if (dnsLeak) s -= 20;
        return Math.Clamp(s, 0, 100);
    }

    private static void Fetch(string ip)
    {
        string body;
        try { body = Http.GetStringAsync("https://api.ipapi.is/?q=" + Uri.EscapeDataString(ip)).Result; }
        catch { return; }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var r = doc.RootElement;
            bool Flag(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

            string? abuse = AbuseLabel(
                r.TryGetProperty("company", out var co)
                && co.TryGetProperty("abuser_score", out var sc) ? sc.GetString() : null);

            bool tor = Flag("is_tor"), abuser = Flag("is_abuser"), bogon = Flag("is_bogon");
            bool vpn = Flag("is_vpn"), proxy = Flag("is_proxy"), dc = Flag("is_datacenter");
            var (verdict, sev) = Classify(tor, abuser, bogon, vpn, proxy, dc, Flag("is_mobile"), abuse);
            Tor = tor; Abuser = abuser; Bogon = bogon; Vpn = vpn; Proxy = proxy; Datacenter = dc;

            Verdict = verdict;
            Abuse = abuse;
            Sev = sev;
            ForIp = ip;
            Interlocked.Increment(ref NetMon.Version);
        }
        catch { }
    }
}

internal static class DnsLeak
{
    public static volatile string? ForIp;
    public static volatile bool Running;
    public static volatile bool Done;
    public static volatile int Resolvers;
    public static volatile string? Where;
    public static volatile bool Leaking;

    private static int _busy;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void Retest()
    {
        ForIp = null;
        Done = false;
        Interlocked.Increment(ref NetMon.Version);
    }

    public static void Want(string? exitIp, string? exitCc)
    {
        if (string.IsNullOrEmpty(exitIp) || string.IsNullOrEmpty(exitCc)) return;
        if (string.Equals(ForIp, exitIp, StringComparison.Ordinal)) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Running = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Run(exitIp!, exitCc!); }
            catch { }
            finally { Running = false; Volatile.Write(ref _busy, 0); }
        });
    }

    private static void Run(string exitIp, string exitCc)
    {
        string id;
        try { id = Http.GetStringAsync("https://bash.ws/id").Result.Trim(); }
        catch { return; }
        if (id.Length == 0) return;

        for (int i = 1; i <= 6; i++)
        {
            try { System.Net.Dns.GetHostEntry($"{i}.{id}.bash.ws"); }
            catch { }
        }

        string body;
        try { body = Http.GetStringAsync($"https://bash.ws/dnsleak/test/{id}?json").Result; }
        catch { return; }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var seen = new List<string>();
            int count = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!e.TryGetProperty("type", out var t) || t.GetString() != "dns") continue;
                count++;
                var cc = (e.TryGetProperty("country", out var c) ? c.GetString() : null)?.ToUpperInvariant();
                if (cc is { Length: > 0 } && !seen.Contains(cc)) seen.Add(cc);
            }
            if (count == 0) return;

            Resolvers = count;
            Where = string.Join(", ", seen);

            Leaking = seen.Count > 0 && seen.Exists(c => !string.Equals(c, exitCc, StringComparison.OrdinalIgnoreCase));
            Done = true;
            ForIp = exitIp;
            Interlocked.Increment(ref NetMon.Version);
        }
        catch { }
    }
}

internal static class IpCountry
{
    public static volatile System.Drawing.Bitmap? Flag;

    public static volatile string? Ip, Cc, Isp, Asn;

    public static volatile string? ApiIp, ApiCc;

    public static bool Split => Ip is { Length: > 0 } a && ApiIp is { Length: > 0 } b
        && !string.Equals(a, b, StringComparison.Ordinal);

    private static Timer? _timer;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static System.Net.Http.HttpClient? _viaProxy;

    public static void Poke() => _timer ??= new Timer(_ => Refresh(), null, 0, 300_000);

    public static void Invalidate() => _timer?.Change(3_000, 300_000);

    private const string Fields = "https://ipwho.is/?fields=ip,country_code,connection";

    private static (string ip, string cc, string isp, string asn)? Ask(System.Net.Http.HttpClient http)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(http.GetStringAsync(Fields).Result);
            var ip = doc.RootElement.GetProperty("ip").GetString();
            var cc = doc.RootElement.GetProperty("country_code").GetString();
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(cc)) return null;
            string isp = "", asn = "";
            if (doc.RootElement.TryGetProperty("connection", out var conn))
            {
                isp = (conn.TryGetProperty("isp", out var i) ? i.GetString() : null)
                      ?? (conn.TryGetProperty("org", out var o) ? o.GetString() : null) ?? "";
                if (conn.TryGetProperty("asn", out var an) && an.TryGetInt32(out int asnNum) && asnNum > 0)
                    asn = "AS" + asnNum;
            }
            return (ip, cc, isp, asn);
        }
        catch { return null; }
    }

    private static void Refresh()
    {
        var direct = Ask(Http);
        if (direct is not { } d) return;
        bool changed = d.ip != Ip;
        Ip = d.ip;
        Cc = d.cc.ToUpperInvariant();
        Isp = d.isp;
        Asn = d.asn;

        var proxy = NetMon.ProxyUrl;
        if (!string.IsNullOrEmpty(proxy))
        {
            try
            {
                _viaProxy ??= new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
                { Proxy = new System.Net.WebProxy(proxy), UseProxy = true })
                { Timeout = TimeSpan.FromSeconds(8) };
                var via = Ask(_viaProxy);
                ApiIp = via?.ip;
                ApiCc = via?.cc.ToUpperInvariant();
            }
            catch { ApiIp = null; ApiCc = null; }
        }
        else { ApiIp = null; ApiCc = null; }

        if (changed || Flag is null)
        {
            try
            {

                var png = Http.GetByteArrayAsync($"https://flagcdn.com/w320/{d.cc.ToLowerInvariant()}.png").Result;
                Flag = new System.Drawing.Bitmap(new System.IO.MemoryStream(png));
            }
            catch { }
        }
        Interlocked.Increment(ref NetMon.Version);
    }
}
