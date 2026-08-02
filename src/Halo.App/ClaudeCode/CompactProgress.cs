using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Halo.ClaudeCode;

internal static class CompactProgress
{

    public static volatile int Percent = -1;
    public static volatile int Tokens = -1;
    public static int Version;

    private const int TypicalSummary = 5700;

    private static int _busy;
    private static long _polledAt;
    private static int _pid;
    private static string? _key;
    private static int _peak;
    private static int _expect = TypicalSummary;
    private static bool _loaded;

    private static readonly string CalibPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "compact-tokens");

    public static void Poke(int pid, string? key)
    {
        Load();
        if (pid <= 0) return;
        Track(pid, key);
        long now = Environment.TickCount64;
        if (now - _polledAt < 600) return;
        _polledAt = now;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var of = _key;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Sample(pid, of); } catch { } finally { Volatile.Write(ref _busy, 0); }
        });
    }

    public static void Done()
    {
        if (_pid == 0 && _key is null && Percent < 0 && Tokens < 0) return;
        if (_peak >= 400) Save(_peak);
        Track(0, null);
    }

        internal static bool Track(int pid, string? key)
    {
        if (pid == _pid && key == _key) return false;
        _pid = pid;
        _key = key;
        _peak = 0;
        Percent = -1;
        Tokens = -1;
        Interlocked.Increment(ref Version);
        return true;
    }

    private static void Sample(int pid, string? of)
    {

        var rows = Interop.ConsoleRead.Tail(pid, 14, below: 2);
        int? bar = null, tokens = null;
        if (rows is not null)
            foreach (var row in rows)
            {
                if (BarShare(row) is { } b) bar = b;
                else if (Streamed(row) is { } n) tokens = n;
            }

        Trace(pid, rows, bar, tokens);
        if (rows is null) return;
        if (of != _key) return;

        int now;
        if (bar is { } pct) { Tokens = -1; now = pct; }
        else if (tokens is { } t)
        {
            if (t > _peak) _peak = t;
            Tokens = t;
            now = Share(t);
        }
        else return;

        Percent = Percent < 0 ? now : Math.Max(Percent, now);
        Interlocked.Increment(ref Version);
    }

    internal static int Share(int tokens, int expect = 0)
    {
        int total = expect > 0 ? expect : _expect;
        return total <= 0 || tokens <= 0 ? -1 : (int)Math.Clamp(100L * tokens / total, 1, 99);
    }

    private const char Filled = (char)0x25B0, Empty = (char)0x25B1;

    internal static int? BarShare(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        int filled = 0, empty = 0;
        foreach (var c in line)
        {
            if (c == Filled) filled++;
            else if (c == Empty) empty++;
        }
        int total = filled + empty;
        return total < 8 ? null : (int)Math.Clamp(100L * filled / total, 0, 100);
    }

    private static readonly Regex Tok = new(@"(\d+(?:\.\d+)?)\s*([kK]?)\s*tokens", RegexOptions.Compiled);

    internal static int? Streamed(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = Tok.Match(line);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        if (m.Groups[2].Value.Length > 0) v *= 1000;
        return v is >= 0 and < 100_000_000 ? (int)v : null;
    }

    internal static string Caption(int percent, int tokens)
        => percent >= 0 ? percent + "%"
         : tokens >= 1000 ? (tokens / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k tok"
         : tokens > 0 ? tokens + " tok"
         : "";

    public static string Caption() => Caption(Percent, Tokens);

    private static void Trace(int pid, string[]? rows, int? bar, int? tokens)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo",
                "compact-debug.txt");
            var sb = new System.Text.StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss.fff} pid={pid} rows={rows?.Length.ToString() ?? "null"} ")
              .Append($"bar={bar?.ToString() ?? "-"} tokens={tokens?.ToString() ?? "-"}").AppendLine();

            if (bar is null && tokens is null && rows is not null)
                foreach (var row in rows)
                    if (row.Length > 0) sb.Append("    | ").AppendLine(row.Length > 110 ? row[..110] : row);
            File.AppendAllText(path, sb.ToString());
        }
        catch { }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try { if (int.TryParse(File.ReadAllText(CalibPath).Trim(), out var v) && v > 0) _expect = v; }
        catch { }
    }

    private static void Save(int tokens)
    {
        _expect = tokens;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CalibPath)!);
            File.WriteAllText(CalibPath, tokens.ToString());
        }
        catch { }
    }
}
