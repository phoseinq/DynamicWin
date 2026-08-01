using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Halo.ClaudeCode;

// How far a running compact has actually got.
//
// There is no file to read for this and no hook that fires while it runs. Checked against the shipped
// claude binary: the only compact_progress events it raises are compact_start / compact_end and a
// hooks_start, nothing lands in the transcript between pre-compact and post-compact, and the figure the
// spinner shows comes from a response_length accumulator that never leaves the process. The terminal is
// where it is published, so the terminal is where it is read - Halo has no console of its own, which is
// exactly what makes AttachConsole to the agent's possible.
//
// What is read is real and live: the tokens of the summary written so far, the same number the user is
// looking at one window over. The DENOMINATOR is the honest weak point - nothing announces how long the
// summary will be - so it is measured rather than invented: the final count of the last compact on this
// machine, written to disk when compact_end lands. Until one has been observed there is no percentage at
// all and the token count itself is shown, which is a real reading that moves.
internal static class CompactProgress
{
    // -1 = nothing known. Volatile: written on the pool, read on the render thread.
    public static volatile int Percent = -1;
    public static volatile int Tokens = -1;
    public static int Version;

    private static int _busy;
    private static long _polledAt;
    private static int _pid;
    private static int _peak;            // the highest reading of THIS compact, for calibration
    private static int _expect;          // what the last compact finished at
    private static bool _loaded;

    private static readonly string CalibPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "compact-tokens");

    // Called once a second from the alert tick while a session is compacting. The scrape itself goes on
    // the pool: attaching to another console is a handful of syscalls, and the render loop is 8ms.
    public static void Poke(int pid)
    {
        Load();
        if (pid <= 0) return;
        if (pid != _pid) Reset(pid);
        long now = Environment.TickCount64;
        if (now - _polledAt < 600) return;
        _polledAt = now;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Sample(pid); } catch { } finally { Volatile.Write(ref _busy, 0); }
        });
    }

    // The compact is over: whatever it reached is what the next one is measured against. Only a plausible
    // reading is kept - a compact the user escaped out of after two seconds must not become the yardstick.
    public static void Done()
    {
        if (_peak >= 400) Save(_peak);
        Reset(0);
        Percent = -1;
        Tokens = -1;
        Interlocked.Increment(ref Version);
    }

    private static void Reset(int pid)
    {
        _pid = pid;
        _peak = 0;
    }

    private static void Sample(int pid)
    {
        var rows = Interop.ConsoleRead.Tail(pid, 8);
        if (rows is null) return;
        int? tokens = null;
        foreach (var row in rows)
            if (Streamed(row) is { } n) tokens = n;   // the spinner is the last such line on screen
        if (tokens is not { } t) return;

        if (t > _peak) _peak = t;
        Tokens = t;
        Percent = _expect > 0 ? (int)Math.Clamp(100L * t / _expect, 1, 99) : -1;
        Interlocked.Increment(ref Version);
    }

    // "(esc to interrupt - 12s - 1.2k tokens)" -> 1200. Pure, and the whole of what is parsed: the arrow,
    // the spinner glyph and the wording around it are all free to change.
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

    // What the pill shows: a percentage once there is something real to divide by, and until then the
    // reading itself. Pure so both branches can be pinned by a test rather than by waiting for a compact.
    internal static string Caption(int percent, int tokens)
        => percent >= 0 ? percent + "%"
         : tokens >= 1000 ? (tokens / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k tok"
         : tokens > 0 ? tokens + " tok"
         : "";

    public static string Caption() => Caption(Percent, Tokens);

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
