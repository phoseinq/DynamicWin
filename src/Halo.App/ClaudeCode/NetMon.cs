using System;
using System.Diagnostics;
using System.Threading;

namespace Halo.ClaudeCode;

// Two-path connectivity samples for the panel graph, both REAL end-to-end HTTPS round-trips
// (a local TUN/proxy can't fake them — no HTTP answer, no sample): "net" = https://www.google.com/generate_204
// (your internet, via Google's connectivity-check endpoint → HTTP 204) and "api" = the real https://api.anthropic.com/v1/messages
// endpoint (GET → 405 when healthy). We probe the message route, NOT the bare root: root stays 404-
// reachable during a messages-endpoint outage/overload, so a root probe reads green while CC is dead.
// If api drops
// while net is fine, the problem is on Anthropic's side/route; if both drop, it's your internet.
// ponytail: samples only while the panel is open (Poke keeps an 8s window), one background thread.
internal static class NetMon
{
    public const int Lost = -1, Empty = -2;
    private static readonly int[] _net = CreateBuf(), _api = CreateBuf();
    private static int _idx;
    private static DateTime _until = DateTime.MinValue;
    private static Thread? _thread;

    public static int Version;

    // always-on health flags (slow background probe even while the pill is collapsed):
    // ApiDown = Anthropic unreachable, NetDown = the internet itself is unreachable,
    // Slow = the internet is reachable but laggy/dropping (drives the "Bad internet :/" banner)
    public static volatile bool ApiDown, NetDown, Slow;
    private const int SlowMs = 1500;   // round-trip beyond this (or a drop) = bad internet
    private static int _slowStreak;    // consecutive bad samples — debounce a single blip

    private static int[] CreateBuf()
    {
        var b = new int[24];
        Array.Fill(b, Empty);
        return b;
    }

    // eager: the heartbeat must run from boot — waiting for the first panel-open meant the
    // collapsed ring/mood never learned about an outage until the user looked (the exact bug)
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

    // oldest→newest
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
            // health heartbeat runs ALWAYS (panel open or not): fresh connection each time —
            // a warm pooled socket can keep answering while every NEW connection gets RST
            // (exactly the ECONNRESET storms CC dies on). The open-panel fast samples below use
            // the pool and must never touch the health flags, or they'd mask that storm.
            if (DateTime.UtcNow - lastBg > TimeSpan.FromSeconds(10))
            {
                lastBg = DateTime.UtcNow;
                int apiMs = HttpLatency(HttpApi, "https://api.anthropic.com/v1/messages", fresh: true);
                bool apiDown = apiMs == Lost;
                int netMs = HttpLatency(HttpNet, "https://www.google.com/generate_204", fresh: true); // always probe → latency for Slow
                bool netDown = apiDown && netMs == Lost; // net "down" only asserted to fingerprint proxy vs internet
                SetHealth(apiDown, netDown);
                bool bad = netMs == Lost || netMs > SlowMs;
                _slowStreak = bad ? _slowStreak + 1 : 0;
                SetSlow(_slowStreak >= 2); // ~20s of bad before we cry wolf; clears on the first good sample

                // Also feed the graph. It used to be fed only by the fast panel-open sampling below (which
                // only runs inside Poke()'s 8s window), so an outage that happened with the panel closed
                // left the ring buffer with nothing in it — reopening the panel afterward to check showed
                // an empty graph even though the collapsed pill's ring/mood had reacted correctly in real
                // time. This heartbeat runs unconditionally, so the graph now always has recent history.
                RecordSample(netMs, apiMs);
            }
            if (DateTime.UtcNow < _until)
            {
                // panel open: fast dual sampling for the graph (pooled = true HTTP round-trip)
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
        IpCountry.Invalidate(); // a route flip (VPN / proxy exit swap) likely moved our exit IP → refresh the flag
        Interlocked.Increment(ref Version); // re-render the ring/mood immediately
    }

    private static void SetSlow(bool slow)
    {
        if (slow == Slow) return;
        Slow = slow;
        Interlocked.Increment(ref Version);
    }

    // api probes go through the SAME proxy Claude Code uses (HTTP(S)_PROXY) — a proxy-side ECONNRESET
    // storm (the kind that kills CC while the raw internet is fine) then turns the ring red here too. A
    // direct probe looked healthy (api root 404 = reachable) while CC died through the proxy — the exact
    // miss the user hit. The NET probe stays DIRECT so "api down but net up" still fingers the proxy/route.
    private static readonly System.Net.Http.HttpClient HttpApi = new(ProxiedHandler())
    { Timeout = TimeSpan.FromSeconds(2.5) };
    private static readonly System.Net.Http.HttpClient HttpNet = new(
        new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(2.5) };

    private static System.Net.Http.SocketsHttpHandler ProxiedHandler()
    {
        var h = new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        // ONLY route through a proxy if one is explicitly configured (most users have none). process env
        // first; fall back to the User-scope var (the logon task may not have inherited it). No proxy set
        // → probe direct, exactly like everyone else — we never silently pull in the system/WinInet proxy.
        var proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
            ?? Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(proxy))
            try { h.Proxy = new System.Net.WebProxy(proxy); h.UseProxy = true; } catch { h.UseProxy = false; }
        else
            h.UseProxy = false; // no proxy configured → direct
        return h;
    }

    private static int HttpLatency(System.Net.Http.HttpClient http, string url, bool fresh = false)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (fresh) req.Headers.ConnectionClose = true; // don't let the pool hide a dead route
            using var resp = http.Send(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            int sc = (int)resp.StatusCode;
            return IsDownStatus(sc) ? Lost : (int)sw.ElapsedMilliseconds;
        }
        catch { return Lost; }
    }

    // down = the request never really reached a healthy Anthropic: 5xx (incl. 529 Overloaded), plus edge
    // blocks CC also can't get through — 403 (Cloudflare/WAF geoblock, "Just a moment"), 407 (proxy
    // auth), 429 (rate-limited). 401/404 = server reachable, just auth/root noise → up. Pulled out of
    // HttpLatency so the status-code mapping is unit-tested without a live HTTP call.
    internal static bool IsDownStatus(int statusCode) =>
        statusCode >= 500 || statusCode == 403 || statusCode == 407 || statusCode == 429;
}

// Faint flag of the country the current (exit) IP sits in — shown next to the panel title.
// One geo lookup every 5 min (no spam); the flag bitmap only changes when the IP does.
internal static class IpCountry
{
    public static volatile System.Drawing.Bitmap? Flag;
    private static string? _ip;
    private static Timer? _timer;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static void Poke() => _timer ??= new Timer(_ => Refresh(), null, 0, 300_000);

    // a route change (VPN / proxy exit swap) flips NetMon's health flags — refetch the flag ~3s after the
    // route settles instead of waiting out the 5-min poll. Repeated flips just defer the one pending fetch;
    // Refresh still only rebuilds the bitmap when the country actually changed. No-op until the panel's
    // first Poke() has armed the timer (the flag isn't shown before then anyway).
    public static void Invalidate() => _timer?.Change(3_000, 300_000);

    private static void Refresh()
    {
        try
        {
            var json = Http.GetStringAsync("https://ipwho.is/?fields=ip,country_code").Result;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var ip = doc.RootElement.GetProperty("ip").GetString();
            if (ip == _ip) return;
            var cc = doc.RootElement.GetProperty("country_code").GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(cc)) return;
            var png = Http.GetByteArrayAsync($"https://flagcdn.com/w80/{cc}.png").Result;
            Flag = new System.Drawing.Bitmap(new System.IO.MemoryStream(png));
            _ip = ip;
            Interlocked.Increment(ref NetMon.Version); // repaint the open panel once
        }
        catch { }
    }
}
