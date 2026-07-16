using System;
using System.Diagnostics;
using System.Threading;

namespace Halo.Codex;

// Independent two-path HTTPS monitor for Codex: 1.1.1.1 measures local internet reachability,
// while chatgpt.com measures the OpenAI path. It samples quickly only while the panel is open.
internal static class CodexNetMon
{
    public const int Lost = -1, Empty = -2;
    private const string ApiTarget = "https://chatgpt.com/";
    private const string NetTarget = "https://1.1.1.1/";

    private static readonly int[] _net = CreateBuffer(), _api = CreateBuffer();
    private static int _index;
    private static DateTime _until = DateTime.MinValue;
    private static Thread? _thread;

    internal static int Version;
    internal static volatile bool ApiDown, NetDown;

    internal static void Poke()
    {
        _until = DateTime.UtcNow.AddSeconds(8);
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
                SetHealth(apiMilliseconds == Lost, netMilliseconds == Lost);
                Interlocked.Increment(ref Version);
                Thread.Sleep(700);
            }
            else if (DateTime.UtcNow - lastBackgroundProbe > TimeSpan.FromSeconds(10))
            {
                lastBackgroundProbe = DateTime.UtcNow;
                var apiDown = HttpLatency(ApiTarget) == Lost;
                var netDown = apiDown && HttpLatency(NetTarget) == Lost;
                SetHealth(apiDown, netDown);
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

    private static int HttpLatency(string url)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = Http.Send(
                new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url),
                System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            return (int)stopwatch.ElapsedMilliseconds;
        }
        catch
        {
            return Lost;
        }
    }
}
