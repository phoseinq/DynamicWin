using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Halo.Widgets;

// The missing number. PartialFiles knows how many bytes have landed but not how many are coming, so a
// percentage is impossible from the filesystem alone. Chromium records both in its own History database,
// and it can be read WHILE the browser holds the file open by opening it read-only with immutable=1 —
// the same trick WpnDb uses on the locked wpndatabase.db, through the same system winsqlite3.dll (no
// NuGet, per the project rule).
//
// Chrome, Edge, Brave, Opera and Vivaldi all share the Chromium `downloads` schema, so one reader covers
// every one of them; only the profile root differs. Firefox is deliberately absent: its downloads are
// annotations in places.sqlite rather than a table, so Firefox gets bytes without a percentage (honest —
// the project forbids inventing numbers).
internal static class BrowserDownloads
{
    // File = the path Chromium is writing to (the .crdownload), Target = the final name it will become
    internal readonly record struct Row(string File, long Received, long Total, string Target);

    private const int OpenReadonly = 0x1, OpenUri = 0x40, RowResult = 100;
    private const double CacheSeconds = 2.5;   // snapshotting a multi-MB History is not a per-frame job
    private const double IdleMinutes = 30;     // profile untouched this long → nothing is downloading there

    private static readonly object _lock = new();
    private static List<Row> _cache = new();
    private static DateTime _cacheAt = DateTime.MinValue;

    // Chromium profile roots. Each holds `Default` and `Profile N` subdirectories with their own History.
    private static IEnumerable<string> ProfileRoots()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(local, @"Google\Chrome\User Data");
        yield return Path.Combine(local, @"Microsoft\Edge\User Data");
        yield return Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data");
        yield return Path.Combine(local, @"Vivaldi\User Data");
        yield return Path.Combine(roaming, @"Opera Software\Opera Stable");
        yield return Path.Combine(roaming, @"Opera Software\Opera GX Stable");
    }

    private static IEnumerable<string> HistoryFiles()
    {
        foreach (var root in ProfileRoots())
        {
            if (!Directory.Exists(root)) continue;
            // Opera keeps History at the root; Chromium keeps one per profile directory
            string direct = Path.Combine(root, "History");
            if (File.Exists(direct)) yield return direct;
            string[] subs;
            try { subs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                string h = Path.Combine(sub, "History");
                if (File.Exists(h)) yield return h;
            }
        }
    }

    // Every in-progress download Chromium currently knows about.
    public static List<Row> InProgress()
    {
        lock (_lock)
            if ((DateTime.UtcNow - _cacheAt).TotalSeconds < CacheSeconds) return _cache;

        var rows = new List<Row>();
        foreach (var db in HistoryFiles())
        {
            try { ReadInto(db, rows); }
            catch { } // a probe failing is normal; degrade silently
        }
        lock (_lock) { _cache = rows; _cacheAt = DateTime.UtcNow; }
        return rows;
    }

    // Total for a partial file, matched by the name Chromium is writing to. Returns 0 when unknown, which
    // the caller must treat as "no percentage" rather than substituting a guess.
    // Total for a partial file on disk. Chromium may key the row by either path, and while a download is
    // running current_path is often empty, so match the partial's real name against target_path too.
    // Returns 0 when unknown, which the caller must treat as "no percentage" rather than a guess.
    public static long TotalFor(string partialPath)
    {
        if (string.IsNullOrEmpty(partialPath)) return 0;
        string partial = Path.GetFileName(partialPath);
        PartialFiles.IsPartial(partial, out string clean);   // "x.iso.crdownload" -> "x.iso"
        foreach (var r in InProgress())
        {
            if (Same(Path.GetFileName(r.File), partial, clean)) return r.Total;
            if (Same(Path.GetFileName(r.Target), partial, clean)) return r.Total;
        }
        return 0;
    }

    private static bool Same(string candidate, string partial, string clean)
        => candidate.Length > 0 &&
           (candidate.Equals(partial, StringComparison.OrdinalIgnoreCase) ||
            (clean.Length > 0 && candidate.Equals(clean, StringComparison.OrdinalIgnoreCase)));

    // The clean final filename for a partial file whose own name is useless ("Unconfirmed 12345.crdownload").
    // The clean final filename for a partial file whose own name is useless ("Unconfirmed 12345.crdownload").
    // Chromium keeps the real destination in target_path, which is why the row carries it separately.
    public static string? NameFor(string partialPath)
    {
        if (string.IsNullOrEmpty(partialPath)) return null;
        string target = Path.GetFileName(partialPath);
        foreach (var r in InProgress())
            if (Path.GetFileName(r.File).Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                string n = Path.GetFileName(r.Target);
                if (n.Length == 0) return null;
                return PartialFiles.IsPartial(n, out string clean) && clean.Length > 0 ? clean : n;
            }
        return null;
    }

    // Reading the live file with immutable=1 was the first attempt and it silently returns only COMPLETED
    // downloads: immutable tells SQLite the file can't change, so it skips the -wal entirely — and an
    // in-progress download is exactly what is still sitting in the WAL, uncheckpointed. Observed live:
    // Chrome downloading, zero rows with state=0.
    // So snapshot the database AND its WAL to temp and open the copy normally, which replays the WAL and
    // reveals the in-progress rows. Copying is allowed while Chrome holds the file (verified).
    private static void ReadInto(string dbPath, List<Row> rows)
    {
        string wal = dbPath + "-wal";
        // Skip profiles that are plainly dormant, but the window has to be generous: Chrome writes the
        // download row once at the start and then leaves History untouched for the whole transfer (seen
        // live: 63s stale while actively downloading). A tight freshness gate skipped the very profile
        // that had the answer. The 2.5s result cache is what actually keeps this cheap.
        try
        {
            var recent = File.Exists(wal) ? File.GetLastWriteTimeUtc(wal) : File.GetLastWriteTimeUtc(dbPath);
            if ((DateTime.UtcNow - recent).TotalMinutes > IdleMinutes) return;
        }
        catch { return; }

        string tmpDir = Path.Combine(Path.GetTempPath(), "halo-dlsnap");
        string snap = Path.Combine(tmpDir, "h" + Math.Abs(dbPath.GetHashCode()) + ".db");
        try
        {
            Directory.CreateDirectory(tmpDir);
            File.Copy(dbPath, snap, overwrite: true);
            if (File.Exists(wal)) File.Copy(wal, snap + "-wal", overwrite: true);

            string uri = "file:///" + snap.Replace('\\', '/').Replace(" ", "%20");
            if (sqlite3_open_v2(Utf8(uri), out IntPtr db, OpenReadonly | OpenUri, IntPtr.Zero) != 0)
            { sqlite3_close(db); return; }
            try
            {
                // No state filter, and received_bytes is ignored. Observed live while Chrome was 60MB into
                // a 100MB file: the row carried total_bytes=104857600 but received_bytes=0 and a state that
                // was not "in progress" — Chrome keeps live progress in memory and only writes the row up
                // front. Filtering on state=0 therefore found nothing, which is why the pill could never
                // show a percentage. The bytes come from the file on disk anyway; the only thing wanted
                // here is the total, so take the newest row per file whatever its state.
                const string sql = @"SELECT target_path, current_path, received_bytes, total_bytes
                                     FROM downloads WHERE total_bytes > 0 ORDER BY id DESC LIMIT 40";
                if (sqlite3_prepare_v2(db, Utf8(sql), -1, out IntPtr st, IntPtr.Zero) != 0) return;
                try
                {
                    while (sqlite3_step(st) == RowResult)
                    {
                        string target = Str(sqlite3_column_text(st, 0));
                        string current = Str(sqlite3_column_text(st, 1));
                        long got = sqlite3_column_int64(st, 2), total = sqlite3_column_int64(st, 3);
                        // current_path is the .crdownload we can see on disk; target_path is the final name
                        rows.Add(new Row(current.Length > 0 ? current : target, got, total, target));
                    }
                }
                finally { sqlite3_finalize(st); }
            }
            finally { sqlite3_close(db); }
        }
        finally
        {
            try { File.Delete(snap); File.Delete(snap + "-wal"); } catch { }
        }
    }

    private static byte[] Utf8(string s)
    {
        var b = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0);
        return b;
    }

    private static string Str(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    private const string Sqlite = "winsqlite3.dll";
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);
}
