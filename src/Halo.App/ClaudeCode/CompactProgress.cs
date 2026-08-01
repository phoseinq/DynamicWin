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
// looking at one window over. The denominator is the honest weak point - nothing announces how long the
// summary will be - and it is measured rather than invented (see TypicalSummary).
internal static class CompactProgress
{
    // -1 = nothing known. Volatile: written on the pool, read on the render thread.
    public static volatile int Percent = -1;
    public static volatile int Tokens = -1;
    public static int Version;

    // What the summary is expected to come to.
    //
    // Claude Code computes no percentage of its own - the spinner carries elapsed time and the streamed
    // count, and that is all there is - so the denominator has to come from somewhere else. It is
    // measured, not guessed: the four compactions in this project's own transcripts produced summaries of
    // 5.0k, 5.3k, 5.9k and 6.5k tokens, a 1.3x spread, because the summarising prompt bounds the shape of
    // what comes back. Hence a real figure to divide by from the FIRST compact, replaced by what this
    // machine actually did as soon as one has been watched to the end.
    //
    // The numerator is exact and starts at zero, which is what makes this work at all: the counter is
    // RESET immediately before compact_start (verified in the shipped binary - `{type:"response_length",
    // op:"reset"}` sits directly before the compact_start event), so during a compact it counts the
    // summary and nothing else, even when an auto-compact interrupts a turn that had already written a lot.
    private const int TypicalSummary = 5700;

    private static int _busy;
    private static long _polledAt;
    private static int _pid;
    private static int _peak;            // the highest reading of THIS compact, for calibration
    private static int _expect = TypicalSummary;
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
        int? tokens = null;
        if (rows is not null)
            foreach (var row in rows)
                if (Streamed(row) is { } n) tokens = n;   // the spinner is the last such line on screen
        // Traced because there is no other way to see this fail: the terminal it reads is not ours, the
        // pill cannot be screenshotted, and "no percentage appeared" has three different causes.
        Trace(pid, rows, tokens);
        if (rows is null || tokens is not { } t) return;

        if (t > _peak) _peak = t;
        Tokens = t;
        // Never backwards, and never 100 until it is actually over: a summary that runs longer than the
        // last one would otherwise sit at 99 having appeared to finish, which is a worse lie than a bar
        // that slows down. compact_end is what takes it off the pill.
        int now = Share(t);
        Percent = Percent < 0 ? now : Math.Max(Percent, now);
        Interlocked.Increment(ref Version);
    }

    // Pure: the reading as a share of what the summary is expected to come to.
    internal static int Share(int tokens, int expect = 0)
    {
        int total = expect > 0 ? expect : _expect;
        return total <= 0 || tokens <= 0 ? -1 : (int)Math.Clamp(100L * tokens / total, 1, 99);
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

    private static void Trace(int pid, string[]? rows, int? tokens)
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo",
                "compact-debug.txt");
            string last = rows is { Length: > 0 } ? rows[^1] : "(none)";
            if (last.Length > 90) last = last[..90];
            File.AppendAllText(path,
                $"{DateTime.Now:HH:mm:ss.fff} pid={pid} rows={rows?.Length.ToString() ?? "null"} " +
                $"tokens={tokens?.ToString() ?? "-"} expect={_expect} last={last}" + Environment.NewLine);
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
