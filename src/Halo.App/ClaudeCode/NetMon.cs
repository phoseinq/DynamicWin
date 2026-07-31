using System;
using System.Collections.Generic;
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
    // Slow = the internet is reachable but laggy/dropping (drives the "Bad internet" banner)
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

    // ONLY route through a proxy if one is explicitly configured (most users have none). Process env first;
    // fall back to the User-scope var (the logon task may not have inherited it). No proxy set → probe
    // direct, exactly like everyone else — we never silently pull in the system/WinInet proxy. Exposed so
    // the exit-IP probe uses the SAME proxy the API probe does; two copies of this lookup could drift and
    // the whole point of comparing the two exits is that one of them is the API's.
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
// What the rest of the internet thinks of the exit, asked only when the user points at the block.
// ipapi.is is the one free endpoint serving the flags that matter here - datacenter / vpn / proxy / tor
// / abuser - over HTTPS and without a key. ip-api.com carries the same flags but its free tier is
// plaintext HTTP, and asking "is my exit private" over a channel the local network can read and rewrite
// is the wrong trade. Every figure below is theirs; none of it is computed here, because a reputation
// score is not something this machine can measure about itself.
internal static class IpRep
{
    public static volatile string? ForIp;    // the address the verdict below belongs to
    public static volatile string? Verdict;  // "residential" / "datacenter" / "vpn exit" / "flagged: tor"
    public static volatile string? Abuse;    // their abuse label for the operator, lowercased
    public static volatile int Sev;          // 0 clean · 1 datacenter · 2 proxy/vpn seen · 3 flagged
    // kept so the mark can be recomputed the moment the dns test lands, without asking them again
    public static volatile bool Tor, Abuser, Bogon, Vpn, Proxy, Datacenter;

    private static int _busy;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // Lazy and idempotent: hovering holds this at one in-flight request per address, and a result is kept
    // until the exit itself changes. A hover is a question, so it costs exactly one lookup to answer.
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

    // Their wording, not their number: the scale behind abuser_score is undocumented, and printing
    // "0.0586" beside their own label of "High" would read as a percentage and mislead. The field arrives
    // as "0.0586 (High)", so the parenthetical is the whole useful part.
    internal static string? AbuseLabel(string? raw)
    {
        if (raw is not { Length: > 0 }) return null;
        int open = raw.IndexOf('('), close = open < 0 ? -1 : raw.IndexOf(')', open);
        if (open < 0 || close <= open) return null;
        string s = raw.Substring(open + 1, close - open - 1).Trim().ToLowerInvariant();
        return s.Length == 0 ? null : s;
    }

    // Ordered by what actually gets you blocked: a flagged address is refused outright, a recognised vpn
    // draws captchas, a plain datacenter is merely noticed, residential passes. Pure so the ordering is a
    // test rather than something to re-derive by hovering an uncapturable window.
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

        // an operator their own data calls a high abuser is the difference between "a datacenter, fine"
        // and "expect captchas", so it lifts a merely-noticed exit into the warning band
        if (sev < 2 && abuse is "high" or "very high") sev = 2;
        return (verdict, sev);
    }

    // The one number here that is OURS. Every input is a real flag from a real lookup, but the weights are
    // a house opinion about what actually gets an address refused, and nothing measures that. So it is
    // presented as a mark out of 100 and never as a percentage of anything, and the line beside it always
    // names the findings that took the points off - a bare score you cannot audit is a magic number.
    internal static int Score(bool tor, bool abuser, bool bogon, bool vpn, bool proxy, bool datacenter,
                              string? abuse, bool split, bool dnsLeak)
    {
        int s = 100;
        if (tor) s -= 55;
        if (abuser) s -= 45;
        if (bogon) s -= 45;
        if (vpn || proxy) s -= 22;          // recognised either way: to a site it is the same fact
        if (datacenter) s -= 14;
        if (abuse == "very high") s -= 22;
        else if (abuse == "high") s -= 14;
        if (split) s -= 12;                 // measured here, not bought: the API leaves by a different door
        if (dnsLeak) s -= 20;               // also measured: resolvers answering from outside the exit
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
            Interlocked.Increment(ref NetMon.Version); // the panel is open and hovering: repaint once
        }
        catch { }
    }
}

// Whether name lookups are leaving by the same door as the traffic. This cannot be measured locally -
// the only way to see which resolver actually asked is to have an authoritative server watch for it - so
// it uses bash.ws, which hands out an id, watches for lookups of <n>.<id>.bash.ws, and reports the
// resolvers that came asking. Run ONLY when the exit block is hovered, once per address, because it is a
// bigger disclosure than the plain IP lookup: it tells a third party who resolves your names.
internal static class DnsLeak
{
    public static volatile string? ForIp;    // the exit this result belongs to
    public static volatile bool Running;
    public static volatile bool Done;
    public static volatile int Resolvers;    // distinct resolvers that answered
    public static volatile string? Where;    // the country codes they sat in, e.g. "TR" or "TR, DE"
    public static volatile bool Leaking;     // at least one resolver outside the exit's own country

    private static int _busy;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Forget the cached answer so the next hover frame runs the test again. The previous verdict is left
    // in place: the row keeps showing it, dimmed, while "testing dns\u2026" is up, which is easier to read
    // than a row that blanks and then fills. An in-flight test is not interrupted - _busy sees to that, and
    // a second press while one is running is a no-op rather than a second round of lookups.
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

        // Each lookup is expected to fail: the names do not resolve to anything. The POINT is the query
        // reaching their authoritative server, which is what identifies the resolver that forwarded it.
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
            if (count == 0) return;   // nothing observed: report nothing rather than a clean bill

            Resolvers = count;
            Where = string.Join(", ", seen);
            // Their own "conclusion" field calls any resolver on a different ASN a leak, which flags simply
            // choosing Cloudflare DNS. The question that matters is whether a lookup left the tunnel, so the
            // test here is the resolver's COUNTRY against the exit's.
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
    // who the exit actually is, not just which flag to draw: the panel says "TR · G-Core Labs" because a
    // flag alone answers "which country" and none of "whose network", which is the part that tells you
    // whether the route is the one you set up.
    public static volatile string? Ip, Cc, Isp, Asn;
    // the same question asked THROUGH the proxy the API probe uses. If the two answers differ, the tool's
    // traffic and everything else are leaving by different doors — measured, not guessed, and only ever
    // asked when a proxy is actually configured.
    public static volatile string? ApiIp, ApiCc;

    public static bool Split => Ip is { Length: > 0 } a && ApiIp is { Length: > 0 } b
        && !string.Equals(a, b, StringComparison.Ordinal);

    private static Timer? _timer;
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static System.Net.Http.HttpClient? _viaProxy;

    public static void Poke() => _timer ??= new Timer(_ => Refresh(), null, 0, 300_000);

    // a route change (VPN / proxy exit swap) flips NetMon's health flags — refetch the flag ~3s after the
    // route settles instead of waiting out the 5-min poll. Repeated flips just defer the one pending fetch;
    // Refresh still only rebuilds the bitmap when the country actually changed. No-op until the panel's
    // first Poke() has armed the timer (the flag isn't shown before then anyway).
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

        // only worth asking twice when the API path is actually routed somewhere else
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
                // w80 was barely above the drawn size, so the panel's own supersampling had nothing spare
                // and detail like the TR star came out mushy. w320 leaves headroom for a resized pill too.
                var png = Http.GetByteArrayAsync($"https://flagcdn.com/w320/{d.cc.ToLowerInvariant()}.png").Result;
                Flag = new System.Drawing.Bitmap(new System.IO.MemoryStream(png));
            }
            catch { }
        }
        Interlocked.Increment(ref NetMon.Version); // repaint the open panel once
    }
}
