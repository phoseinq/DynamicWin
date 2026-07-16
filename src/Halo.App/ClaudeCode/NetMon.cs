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
                // collapsed: slow heartbeat so the ring still knows when the API dies
                lastBg = DateTime.UtcNow;
                bool apiDown = HttpLatency("https://api.anthropic.com/") == Lost;
                bool netDown = apiDown && HttpLatency("https://1.1.1.1/") == Lost;
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

    private static int HttpLatency(string url)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var resp = Http.Send(
                new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url),
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            return (int)sw.ElapsedMilliseconds; // any HTTP status = the server actually answered
        }
        catch { return Lost; }
    }
}
