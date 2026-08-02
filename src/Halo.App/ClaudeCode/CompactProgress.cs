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
// And it turns out to publish a real percentage after all - just not as a number. While a compact runs the
// spinner draws a FORTY-CELL BAR of filled and empty parallelograms (U+25B0 / U+25B1):
//
//     * Compacting conversation...
//       [][][][][][][][]......................
//
// so the progress is exact: filled / 40. Found by capturing the terminal once a second through a real
// compact and looking, after two rounds of assuming the spinner carried the token count it shows during
// an ordinary turn. It does not - during a compact there is no number on screen at all, which is why the
// pill kept showing nothing while the bar in the terminal climbed to 40%.
//
// The token reading below is kept as the fallback for a build that shows one instead, and only that path
// needs a denominator to guess at.
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
    private static string? _key;         // the stamp pre-compact wrote: which compact this reading is of
    private static int _peak;            // the highest reading of THIS compact, for calibration
    private static int _expect = TypicalSummary;
    private static bool _loaded;

    private static readonly string CalibPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "compact-tokens");

    // Called once a second from the alert tick while a session is compacting. The scrape itself goes on
    // the pool: attaching to another console is a handful of syscalls, and the render loop is 8ms.
    public static void Poke(int pid, string? key)
    {
        Load();
        if (pid <= 0) return;
        Track(pid, key);
        long now = Environment.TickCount64;
        if (now - _polledAt < 600) return;
        _polledAt = now;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var of = _key;   // which compact this sample belongs to; a reset while it is in flight voids it
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Sample(pid, of); } catch { } finally { Volatile.Write(ref _busy, 0); }
        });
    }

    // The compact is over: whatever it reached is what the next one is measured against. Only a plausible
    // reading is kept - a compact the user escaped out of after two seconds must not become the yardstick.
    public static void Done()
    {
        if (_pid == 0 && _key is null && Percent < 0 && Tokens < 0) return;  // already clear; called every tick
        if (_peak >= 400) Save(_peak);
        Track(0, null);
    }

    /// <summary>
    /// Point the reader at a compact, clearing the previous one's figure. A compact is identified by the
    /// stamp pre-compact wrote, NOT by the pid: a second /compact in the same terminal is the same
    /// process, so keying on the pid alone left the previous run's percentage standing. Reported live -
    /// a compact cancelled at 40% was still reading 40% when the next one started, instead of opening
    /// at nothing. The other half of that bug was Done() being called only when a TOKEN reading was on
    /// screen, while the bar path deliberately leaves Tokens at -1, so the clearing branch never ran.
    /// </summary>
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
        // 14 rows and two past the cursor: the spinner sits above the prompt, and a TUI parks the cursor
        // in whatever it wants typed into, which is not always the bottom of what it is drawing.
        var rows = Interop.ConsoleRead.Tail(pid, 14, below: 2);
        int? bar = null, tokens = null;
        if (rows is not null)
            foreach (var row in rows)
            {
                if (BarShare(row) is { } b) bar = b;              // the bar wins: it is the real figure
                else if (Streamed(row) is { } n) tokens = n;      // the spinner is the last such line
            }
        // Traced because there is no other way to see this fail: the terminal it reads is not ours, the
        // pill cannot be screenshotted, and "no percentage appeared" has three different causes.
        Trace(pid, rows, bar, tokens);
        if (rows is null) return;
        if (of != _key) return;   // the compact this was read for ended while the read was in flight

        int now;
        if (bar is { } pct) { Tokens = -1; now = pct; }
        else if (tokens is { } t)
        {
            if (t > _peak) _peak = t;
            Tokens = t;
            now = Share(t);
        }
        else return;

        // never backwards: a bar that is redrawn mid-frame can read short for one sample, and a figure
        // that steps back looks like the work is being undone
        Percent = Percent < 0 ? now : Math.Max(Percent, now);
        Interlocked.Increment(ref Version);
    }

    // Pure: the reading as a share of what the summary is expected to come to.
    internal static int Share(int tokens, int expect = 0)
    {
        int total = expect > 0 ? expect : _expect;
        return total <= 0 || tokens <= 0 ? -1 : (int)Math.Clamp(100L * tokens / total, 1, 99);
    }

    // The bar Claude Code actually draws, as a percentage of itself. Nothing else on screen is made of
    // these two glyphs, so their presence IS the recognition - no wording, no spinner glyph and no cell
    // count is part of the contract, and a build that draws a bar of a different length still measures.
    // by code point, not by glyph: source files here stay ASCII, and an editor that resolves an escape
    // puts the real character back the moment the line is written
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
            // every row, but only when nothing parsed - that is the case that cannot be diagnosed from a
            // summary line, and a working sample would fill the file with the same screen once a second
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
