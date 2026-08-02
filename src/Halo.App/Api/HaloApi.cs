using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Halo.Api;

// Halo's own local API: post a notification, ask a question, read what the pill is showing.
//
// TcpListener with a hand-written HTTP/1.1 reader, not HttpListener. HttpListener goes through http.sys,
// which needs a URL ACL reservation for any prefix an unelevated process wants - so `netsh http add
// urlacl` at install time, running as admin, for a per-user app that deliberately never asks for
// elevation. Binding a loopback TCP port needs nothing. The parser below is small because the surface is
// small: a request line, headers, an optional Content-Length body. No chunked encoding, no keep-alive.
//
// Bound to 127.0.0.1 explicitly rather than IPAddress.Any. That is the whole security model together with
// the token: nothing off this machine can reach it at all, so the token is protection against other
// programs on the same box rather than against the network.
internal sealed class HaloApi : IDisposable
{
    internal const int DefaultPort = 7317;

    private readonly Func<Config> _config;
    private readonly IHaloHost _host;
    private TcpListener? _listener;
    private int _port;
    private volatile bool _stop;

    // Capabilities are groups rather than one switch per route: "may it read what is on screen" and "may
    // it press buttons" are the questions a person actually has an opinion about, and a settings page with
    // eleven checkboxes for eleven URLs is a page nobody finishes reading.
    internal sealed record Config(
        bool Enabled, int Port, string Token,
        bool Notify, bool Ask, bool State, bool Control, bool Settings);

    internal HaloApi(Func<Config> config, IHaloHost host)
    {
        _config = config;
        _host = host;
    }

    internal string? LastError { get; private set; }

    // Called whenever settings change as well as at startup, so switching the API on in the panel starts
    // it and switching it off actually closes the socket rather than leaving it listening and refusing.
    internal void Reconcile()
    {
        try
        {
            var config = _config();
            if (!config.Enabled || config.Token.Length == 0) { Stop(); return; }
            if (_listener != null && _port == config.Port) return;
            Stop();
            Start(config.Port);
        }
        catch (Exception e) { LastError = e.Message; }
    }

    private void Start(int port)
    {
        try
        {
            _stop = false;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _port = port;
            LastError = null;
            var listener = _listener;
            var thread = new System.Threading.Thread(() => Accept(listener)) { IsBackground = true };
            thread.Start();
        }
        catch (Exception e)
        {
            LastError = e.Message;   // a port already in use is the common one, and the panel shows it
            _listener = null;
        }
    }

    private void Stop()
    {
        _stop = true;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _port = 0;
    }

    private void Accept(TcpListener listener)
    {
        while (!_stop)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }   // Stop() closed it, which is the normal way out of this loop
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using var stream = client.GetStream();
                var request = Request.Read(stream);
                if (request is null) return;
                var (status, body) = Route(request);
                Write(stream, status, body);
            }
            catch { }   // a malformed caller must not take the pill down with it
        }
    }

    private (int Status, JsonObject Body) Route(Request request)
    {
        var config = _config();
        if (!Constant(request.Token, config.Token))
            return (401, Error("bad or missing token"));

        string path = request.Path.TrimEnd('/');
        if (path.Length == 0) path = "/";

        // GET /health answers regardless of which capabilities are off: a caller has to be able to tell
        // "Halo is not listening" from "Halo is listening and will not do that for you".
        if (request.Method == "GET" && path == "/health")
            return (200, new JsonObject
            {
                ["ok"] = true,
                ["product"] = "Halo",
                ["capabilities"] = new JsonArray(
                    config.Notify ? "notify" : null, config.Ask ? "ask" : null,
                    config.State ? "state" : null, config.Control ? "control" : null,
                    config.Settings ? "settings" : null),
            });

        return (request.Method, path) switch
        {
            ("POST", "/notify") => config.Notify ? Notify(request) : Off(),
            ("POST", "/ask") => config.Ask ? Ask(request) : Off(),
            ("GET", _) when path.StartsWith("/ask/", StringComparison.Ordinal)
                => config.Ask ? Answer(path[5..]) : Off(),

            ("GET", "/state") => config.State ? (200, _host.State()) : Off(),
            ("GET", "/media") => config.State ? (200, _host.Media()) : Off(),
            ("GET", "/agents") => config.State ? (200, _host.Agents()) : Off(),
            ("GET", "/tray") => config.State ? (200, _host.Tray()) : Off(),

            ("POST", "/media") => config.Control ? MediaControl(request) : Off(),
            ("POST", "/pill") => config.Control ? Pill(request) : Off(),
            ("POST", "/tray") => config.Control ? TrayAdd(request) : Off(),

            ("GET", "/settings") => config.Settings ? (200, _host.Settings()) : Off(),
            ("PATCH", "/settings") => config.Settings ? Patch(request) : Off(),

            _ => (404, Error("no such endpoint")),
        };
    }

    private static (int, JsonObject) Off()
        => (403, Error("that capability is switched off in Halo's settings"));

    private (int, JsonObject) MediaControl(Request request)
    {
        string action = Str(request.Json, "action");
        if (action.Length == 0) return (400, Error("action is required"));
        int slot = Int(request.Json, "slot", -1);
        bool sent = _host.MediaControl(action, slot);
        return sent
            ? (200, new JsonObject { ["ok"] = true })
            : (400, Error("no session to control, or unknown action"));
    }

    private (int, JsonObject) Pill(Request request)
    {
        string action = Str(request.Json, "action");
        if (action.Length == 0) return (400, Error("action is required"));
        return _host.Pill(action)
            ? (200, new JsonObject { ["ok"] = true })
            : (400, Error("unknown action"));
    }

    private (int, JsonObject) TrayAdd(Request request)
    {
        var paths = new List<string>();
        if (request.Json?["paths"] is JsonArray array)
            foreach (var node in array)
                if (node?.GetValue<string>() is { Length: > 0 } p) paths.Add(p);
        else if (Str(request.Json, "path") is { Length: > 0 } single) paths.Add(single);
        if (paths.Count == 0) return (400, Error("paths is required"));

        int added = _host.TrayAdd(paths);
        // A path that does not exist is reported rather than silently dropped: the tray refuses those, and
        // a caller that got 200 with nothing added would have no way to find that out.
        return (200, new JsonObject { ["added"] = added, ["skipped"] = paths.Count - added });
    }

    private (int, JsonObject) Patch(Request request)
    {
        if (request.Json?["values"] is not JsonObject values || values.Count == 0)
            return (400, Error("values is required"));
        int written = _host.SettingsPatch(values);
        return (200, new JsonObject { ["written"] = written });
    }

    private (int, JsonObject) Notify(Request request)
    {
        var json = request.Json;
        string title = Str(json, "title");
        if (title.Length == 0) return (400, Error("title is required"));
        _host.Notify(new NotifyRequest(
            Str(json, "app", "Halo"), title, Str(json, "body"),
            Int(json, "seconds", 6), Str(json, "code"), Str(json, "launch")));
        return (200, new JsonObject { ["ok"] = true });
    }

    // Written into the directory the pill's AskStore already watches, in the format it already parses.
    // The tool name is ours rather than AskUserQuestion, and that difference is load-bearing: a question
    // is answered by typing a number into an agent's terminal, which an HTTP caller has not got. Anything
    // else is answered by writing answer-{nonce}.json - which is exactly the file this endpoint's caller
    // then polls for.
    private (int, JsonObject) Ask(Request request)
    {
        var json = request.Json;
        string question = Str(json, "question");
        if (question.Length == 0) return (400, Error("question is required"));

        var options = new JsonArray();
        if (json?["options"] is JsonArray given)
            foreach (var node in given)
            {
                string label = node is JsonObject o ? Str(o, "label") : node?.GetValue<string>() ?? "";
                if (label.Length == 0) continue;
                options.Add(new JsonObject
                {
                    ["label"] = label,
                    ["description"] = node is JsonObject d ? Str(d, "description") : "",
                });
            }
        if (options.Count == 0) return (400, Error("at least one option is required"));

        string nonce = Guid.NewGuid().ToString("n");
        int seconds = Math.Clamp(Int(json, "timeoutSeconds", 300), 10, 3600);
        var envelope = new JsonObject
        {
            ["nonce"] = nonce,
            ["pid"] = 0,
            ["session"] = Str(json, "app", "api"),
            ["tool"] = "HaloApi",
            ["target"] = Str(json, "app", "API"),
            ["question"] = question,
            ["options"] = options,
            ["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("o"),
        };
        try
        {
            Directory.CreateDirectory(AskDir);
            string path = Path.Combine(AskDir, $"ask-{nonce}.json");
            File.WriteAllText(path + ".tmp", envelope.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) { return (500, Error(e.Message)); }

        return (200, new JsonObject { ["nonce"] = nonce, ["poll"] = $"/ask/{nonce}" });
    }

    private (int, JsonObject) Answer(string nonce)
    {
        if (nonce.Length == 0 || nonce.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return (400, Error("bad nonce"));
        try
        {
            string answer = Path.Combine(AskDir, $"answer-{nonce}.json");
            if (File.Exists(answer))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(answer)) as JsonObject;
                try { File.Delete(answer); } catch { }
                try { File.Delete(Path.Combine(AskDir, $"ask-{nonce}.json")); } catch { }
                return (200, new JsonObject { ["answered"] = true, ["choice"] = Str(parsed, "decision") });
            }
            bool live = File.Exists(Path.Combine(AskDir, $"ask-{nonce}.json"));
            return (200, new JsonObject { ["answered"] = false, ["pending"] = live });
        }
        catch (Exception e) { return (500, Error(e.Message)); }
    }

    private static string AskDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch");

    // Length-independent compare. Overkill against a local caller, and it costs nothing; a token check
    // that returns early on the first wrong byte is the kind of thing that gets copied somewhere it
    // matters.
    private static bool Constant(string a, string b)
    {
        if (a.Length != b.Length || b.Length == 0) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    private static string Str(JsonObject? o, string key, string fallback = "")
        => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : fallback;

    private static int Int(JsonObject? o, string key, int fallback)
        => o?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

    private static void Write(Stream stream, int status, JsonObject body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body.ToJsonString());
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(payload.Length).Append("\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        byte[] header = Encoding.ASCII.GetBytes(head);
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        _ => "Internal Server Error",
    };

    private sealed record Request(string Method, string Path, string Token, JsonObject? Json)
    {
        private const int MaxBody = 64 * 1024;   // a notification is a few hundred bytes; this is generous

        internal static Request? Read(Stream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            // read to the blank line that ends the headers, byte at a time: the bodies here are tiny and a
            // buffered reader would have to hand back whatever it over-read
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (stream.Read(one, 0, 1) != 1) return null;
                head.Append((char)one[0]);
                if (head.Length > 8192) return null;
            }

            var lines = head.ToString().Split("\r\n");
            var start = lines[0].Split(' ');
            if (start.Length < 2) return null;

            string token = "";
            int length = 0;
            foreach (var line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    token = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
                else if (name.Equals("X-Halo-Token", StringComparison.OrdinalIgnoreCase)) token = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out length);
            }

            JsonObject? json = null;
            if (length is > 0 and <= MaxBody)
            {
                var body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(body, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                try { json = JsonNode.Parse(Encoding.UTF8.GetString(body, 0, read)) as JsonObject; }
                catch { return null; }
            }

            string path = start[1];
            int query = path.IndexOf('?');
            if (query >= 0) path = path[..query];
            return new Request(start[0].ToUpperInvariant(), path, token, json);
        }
    }

    public void Dispose() => Stop();
}
