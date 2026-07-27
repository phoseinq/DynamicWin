using System;
using System.Diagnostics;
using System.Threading;

namespace Halo.Codex;

// Independent two-path HTTPS monitor for Codex: google.com/generate_204 measures local internet reachability,
// while chatgpt.com measures the OpenAI path. It samples quickly only while the panel is open.
internal static class CodexNetMon
{
    public const int Lost = -1, Empty = -2;
    // probe the REAL Codex endpoint (GET → 405 when healthy), NOT the bare chatgpt.com root: the root/
    // marketing edge stays 200-reachable during a backend outage, so a root probe reads green while
    // Codex is dead. This is the /backend-api/codex/responses route the CLI actually POSTs to.
    private const string ApiTarget = "https://chatgpt.com/backend-api/codex/responses";
    private const string NetTarget = "https://www.google.com/generate_204";

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
                var apiMs = HttpLatency(ApiTarget, fresh: true);
                var apiDown = apiMs == Lost;
                var netMs = HttpLatency(NetTarget, fresh: true);
                var netDown = apiDown && netMs == Lost;
                SetHealth(apiDown, netDown);

                // Also feed the graph. It used to be fed only by the fast panel-open sampling below (only
                // active inside Poke()'s 8s window), so an outage while the panel was closed left the ring
                // buffer empty — reopening the panel afterward showed nothing even though ApiDown had
                // already reacted in real time. This heartbeat runs unconditionally.
                RecordSample(netMs, apiMs);
            }
            if (DateTime.UtcNow < _until)
            {
                var apiMilliseconds = Lost;
                var apiProbe = new Thread(() => apiMilliseconds = HttpLatency(ApiTarget)) { IsBackground = true };
                apiProbe.Start();
                var netMilliseconds = HttpLatency(NetTarget);
                apiProbe.Join(2600);

                RecordSample(netMilliseconds, apiMilliseconds);
                Thread.Sleep(700);
            }
            else
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void RecordSample(int netMs, int apiMs)
    {
        lock (_net)
        {
            _net[_index] = netMs;
            _api[_index] = apiMs;
            _index = (_index + 1) % _net.Length;
        }
        Interlocked.Increment(ref Version);
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
            return IsDownStatus(sc) ? Lost : (int)stopwatch.ElapsedMilliseconds;
        }
        catch
        {
            return Lost;
        }
    }

    // down = never really reached a healthy API: 5xx (incl. 529 Overloaded), plus edge blocks the client
    // also can't get through — 403 (Cloudflare/WAF geoblock), 407 (proxy auth), 429 (rate-limited).
    // 401/404 = server reachable, just auth/root noise → up. Pulled out so the mapping is unit-tested
    // without a live HTTP call.
    internal static bool IsDownStatus(int statusCode) =>
        statusCode >= 500 || statusCode == 403 || statusCode == 407 || statusCode == 429;
}
