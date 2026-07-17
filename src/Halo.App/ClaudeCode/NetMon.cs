using System;
using System.Diagnostics;
using System.Threading;

namespace Halo.ClaudeCode;

// Two-path connectivity samples for the panel graph, both REAL end-to-end HTTPS round-trips
// (a local TUN/proxy can't fake them — no HTTP answer, no sample): "net" = https://1.1.1.1
// (your internet, via the Cloudflare edge) and "api" = https://api.anthropic.com. If api drops
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
    // ApiDown = Anthropic unreachable, NetDown = the internet itself is unreachable
    public static volatile bool ApiDown, NetDown;

    private static int[] CreateBuf()
    {
        var b = new int[24];
        Array.Fill(b, Empty);
        return b;
    }

    public static void Poke()
    {
        IpCountry.Poke();
        _until = DateTime.UtcNow.AddSeconds(8);
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
            if (DateTime.UtcNow < _until)
            {
                // panel open: fast dual sampling for the graph
                int apiMs = Lost;
                var apiTask = new Thread(() => apiMs = HttpLatency("https://api.anthropic.com/")) { IsBackground = true };
                apiTask.Start();
                int netMs = HttpLatency("https://1.1.1.1/");
                apiTask.Join(2600);

                lock (_net) { _net[_idx] = netMs; _api[_idx] = apiMs; _idx = (_idx + 1) % _net.Length; }
                SetHealth(apiMs == Lost, netMs == Lost);
                Interlocked.Increment(ref Version);
                Thread.Sleep(700);
            }
            else if (DateTime.UtcNow - lastBg > TimeSpan.FromSeconds(10))
            {
                // collapsed: slow heartbeat so the ring still knows when the API dies.
                // fresh connection each time — a warm pooled socket can keep answering while
                // every NEW connection gets RST (exactly the ECONNRESET storms CC dies on)
                lastBg = DateTime.UtcNow;
                bool apiDown = HttpLatency("https://api.anthropic.com/", fresh: true) == Lost;
                bool netDown = apiDown && HttpLatency("https://1.1.1.1/", fresh: true) == Lost;
                SetHealth(apiDown, netDown);
            }
            else Thread.Sleep(300);
        }
    }

    private static void SetHealth(bool apiDown, bool netDown)
    {
        if (apiDown == ApiDown && netDown == NetDown) return;
        ApiDown = apiDown;
        NetDown = netDown;
        Interlocked.Increment(ref Version); // re-render the ring/mood immediately
    }

    // keep-alive client: after the first TLS handshake, each sample is one true HTTP round-trip
    private static readonly System.Net.Http.HttpClient Http = new(
        new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = TimeSpan.FromSeconds(2.5) };

    private static int HttpLatency(string url, bool fresh = false)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (fresh) req.Headers.ConnectionClose = true; // don't let the pool hide a dead route
            using var resp = Http.Send(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            // 4xx = the server is fine (auth/route noise); 5xx (incl. 529 Overloaded) = it's down
            return (int)resp.StatusCode >= 500 ? Lost : (int)sw.ElapsedMilliseconds;
        }
        catch { return Lost; }
    }
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
