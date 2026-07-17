using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;

namespace Halo.ClaudeCode;

// Real 5h / weekly usage from GET api.anthropic.com/api/oauth/usage — the same source the claude.ai
// usage page reads, so the numbers match the site exactly. Zero token cost (no inference), so we
// fetch eagerly: once at startup and whenever the panel opens (throttled to 20s).
internal static class Limits
{
    public static float FiveHour = -1, Week = -1;          // 0..1 utilization, -1 = unknown
    public static DateTimeOffset FiveHourReset, WeekReset;
    public static bool Failed;                             // last fetch errored (e.g. no internet)

    // last good values survive app restarts (the endpoint 429s if hammered; don't start blank)
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "usage-cache.json");

    static Limits()
    {
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(CachePath));
            FiveHour = n?["fiveHour"]?.GetValue<float>() ?? -1;
            Week = n?["week"]?.GetValue<float>() ?? -1;
            DateTimeOffset.TryParse(n?["fiveHourReset"]?.GetValue<string>(), out FiveHourReset);
            DateTimeOffset.TryParse(n?["weekReset"]?.GetValue<string>(), out WeekReset);
            if (DateTimeOffset.TryParse(n?["savedAt"]?.GetValue<string>(), out var sa))
                LastSuccess = sa.UtcDateTime;
        }
        catch { }
    }

    private static void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, new JsonObject
            {
                ["fiveHour"] = FiveHour,
                ["week"] = Week,
                ["fiveHourReset"] = FiveHourReset.ToString("o"),
                ["weekReset"] = WeekReset.ToString("o"),
                ["savedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            }.ToJsonString());
        }
        catch { }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static DateTime _last = DateTime.MinValue;
    private static TimeSpan _cooldown = TimeSpan.FromSeconds(30);

    // without this the numbers only refresh on panel-open — "updated 59m ago" if you never reopen.
    // the endpoint costs no tokens, so a slow heartbeat is safe (429 backoff still applies).
    private static readonly Timer Heartbeat =
        new(_ => Fetch(force: false), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    private static int _busy;
    private static readonly List<DateTime> _opens = new();

    public static DateTime LastSuccess = DateTime.MinValue; // for "updated Xm ago"

    public static void Poke() => Fetch(force: false);

    // spam guard: opening the panel refreshes, but >2 opens inside a minute serve the cache
    // until the data is 5+ minutes old (or the refresh button forces it)
    public static void OnPanelOpen()
    {
        var now = DateTime.UtcNow;
        lock (_opens)
        {
            _opens.Add(now);
            _opens.RemoveAll(t => (now - t).TotalSeconds > 60);
            if (_opens.Count > 2 && now - LastSuccess < TimeSpan.FromMinutes(5)) return;
        }
        Fetch(force: false);
    }

    public static void ForceRefresh() => Fetch(force: true);

    private static void Fetch(bool force)
    {
        var now = DateTime.UtcNow;
        if (now - _last < (force ? TimeSpan.FromSeconds(5) : _cooldown)) return; // even force can't hammer
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _last = now;
        ThreadPool.QueueUserWorkItem(_ => { try { Refresh(); } finally { _busy = 0; } });
    }

    private static void Refresh()
    {
        try
        {
            var cred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");
            var tok = JsonNode.Parse(File.ReadAllText(cred))?["claudeAiOauth"]?["accessToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tok)) return;

            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + tok);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            using var resp = Http.Send(req);
            if ((int)resp.StatusCode == 429)
            {
                // the free usage endpoint rate-limits itself long before the account does — fall
                // back to a 1-token probe whose unified headers carry the REAL utilizations
                Probe(tok);
                _cooldown = TimeSpan.FromMinutes(2);
                return;
            }
            _cooldown = TimeSpan.FromSeconds(30);
            var root = JsonNode.Parse(resp.Content.ReadAsStream());

            (float u, DateTimeOffset r) Bucket(string key)
            {
                var n = root?[key];
                float u = n?["utilization"]?.GetValue<float>() ?? -1;
                DateTimeOffset.TryParse(n?["resets_at"]?.GetValue<string>(), out var r);
                return (u < 0 ? -1 : u / 100f, r);
            }
            var (u5, r5) = Bucket("five_hour");
            if (u5 >= 0) { FiveHour = u5; FiveHourReset = r5; } // never clobber good data with -1
            var (u7, r7) = Bucket("seven_day");
            if (u7 >= 0) { Week = u7; WeekReset = r7; }
            if (u5 >= 0 || u7 >= 0) { LastSuccess = DateTime.UtcNow; SaveCache(); }
            Failed = false;
        }
        catch { Failed = true; }
    }

    // POST a max_tokens:1 haiku message and read the anthropic-ratelimit-unified-* headers —
    // fractions 0..1 + unix-second resets, present even when the call itself 429s. ~1 token.
    private static void Probe(string tok)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + tok);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Content = new System.Net.Http.StringContent(
                "{\"model\":\"claude-haiku-4-5-20251001\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
                System.Text.Encoding.UTF8, "application/json");
            using var resp = Http.Send(req);

            float H(string n) => resp.Headers.TryGetValues(n, out var v)
                && float.TryParse(System.Linq.Enumerable.First(v),
                    System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : -1f;
            DateTimeOffset R(string n) => resp.Headers.TryGetValues(n, out var v)
                && long.TryParse(System.Linq.Enumerable.First(v), out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s) : default;

            float u5 = H("anthropic-ratelimit-unified-5h-utilization");
            float u7 = H("anthropic-ratelimit-unified-7d-utilization");
            if (u5 >= 0) { FiveHour = Math.Min(1f, u5); FiveHourReset = R("anthropic-ratelimit-unified-5h-reset"); }
            if (u7 >= 0) { Week = Math.Min(1f, u7); WeekReset = R("anthropic-ratelimit-unified-7d-reset"); }
            if (u5 >= 0 || u7 >= 0) { LastSuccess = DateTime.UtcNow; SaveCache(); Failed = false; }
        }
        catch { Failed = true; }
    }
}
