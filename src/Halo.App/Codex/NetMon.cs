using System;
using System.Diagnostics;
using System.Threading;

namespace Halo.Codex;

// Independent two-path HTTPS monitor for Codex: 1.1.1.1 measures local internet reachability,
// while chatgpt.com measures the OpenAI path. It samples quickly only while the panel is open.
internal static class CodexNetMon
{
    public const int Lost = -1, Empty = -2;
    // probe the REAL Codex endpoint (GET → 405 when healthy), NOT the bare chatgpt.com root: the root/
    // marketing edge stays 200-reachable during a backend outage, so a root probe reads green while
    // Codex is dead. This is the /backend-api/codex/responses route the CLI actually POSTs to.
    private const string ApiTarget = "https://chatgpt.com/backend-api/codex/responses";
    private const string NetTarget = "https://1.1.1.1/";

    private static readonly int[] _net = CreateBuffer(), _api = CreateBuffer();
    private static int _index;
    private static DateTime _until = DateTime.MinValue;
    private static Thread? _thread;

    internal static int Version;
    internal static volatile bool ApiDown, NetDown;

    // eager: the heartbeat must run from boot — waiting for the first panel-open meant the
    // collapsed ring/mood never learned about an outage until the user looked
    static CodexNetMon() => EnsureThread();

    internal static void Poke()
    {
        _until = DateTime.UtcNow.AddSeconds(8);
        EnsureThread();
    }

    private static void EnsureThread()
    {
        if (_thread is null)
        {
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }
    }

    // Oldest to newest; copies prevent UI drawing from racing a sample write.
    internal static (int[] net, int[] api) Snapshot()
    {
        lock (_net)
        {
            var net = new int[_net.Length];
            var api = new int[_api.Length];
            for (var i = 0; i < _net.Length; i++)
            {
                net[i] = _net[(_index + i) % _net.Length];
                api[i] = _api[(_index + i) % _api.Length];
            }
            return (net, api);
        }
    }

    private static int[] CreateBuffer()
    {
        var buffer = new int[24];
        Array.Fill(buffer, Empty);
        return buffer;
    }

    private static void Loop()
    {
        var lastBackgroundProbe = DateTime.MinValue;
        while (true)
        {
            // health heartbeat runs ALWAYS (panel open or not): fresh connection each time — a warm
            // pooled socket can keep answering while every NEW connection gets RST. The open-panel
            // fast samples below use the pool and must never touch the health flags.
            if (DateTime.UtcNow - lastBackgroundProbe > TimeSpan.FromSeconds(10))
            {
                lastBackgroundProbe = DateTime.UtcNow;
                var apiDown = HttpLatency(ApiTarget, fresh: true) == Lost;
                var netDown = apiDown && HttpLatency(NetTarget, fresh: true) == Lost;
                SetHealth(apiDown, netDown);
            }
            if (DateTime.UtcNow < _until)
            {
                var apiMilliseconds = Lost;
                var apiProbe = new Thread(() => apiMilliseconds = HttpLatency(ApiTarget)) { IsBackground = true };
                apiProbe.Start();
                var netMilliseconds = HttpLatency(NetTarget);
                apiProbe.Join(2600);

                lock (_net)
                {
                    _net[_index] = netMilliseconds;
                    _api[_index] = apiMilliseconds;
                    _index = (_index + 1) % _net.Length;
                }
                Interlocked.Increment(ref Version);
                Thread.Sleep(700);
            }
            else
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void SetHealth(bool apiDown, bool netDown)
    {
        if (apiDown == ApiDown && netDown == NetDown)
            return;

        ApiDown = apiDown;
        NetDown = netDown;
        Interlocked.Increment(ref Version);
    }

    private static readonly System.Net.Http.HttpClient Http = new(
        new System.Net.Http.SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = TimeSpan.FromSeconds(2.5) };

    private static int HttpLatency(string url, bool fresh = false)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
            if (fresh) request.Headers.ConnectionClose = true; // don't let the pool hide a dead route
            using var response = Http.Send(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            int sc = (int)response.StatusCode;
            // down = never really reached a healthy API: 5xx (incl. 529 Overloaded), plus edge blocks the
            // client also can't get through — 403 (Cloudflare/WAF geoblock), 407 (proxy auth), 429 (rate-limited).
            // 401/404 = server reachable, just auth/root noise → up.
            return sc >= 500 || sc == 403 || sc == 407 || sc == 429 ? Lost : (int)stopwatch.ElapsedMilliseconds;
        }
        catch
        {
            return Lost;
        }
    }
}
