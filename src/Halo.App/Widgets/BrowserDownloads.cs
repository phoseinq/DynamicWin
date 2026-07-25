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
    internal readonly record struct Row(string File, long Received, long Total);

    private const int OpenReadonly = 0x1, OpenUri = 0x40, RowResult = 100;
    private const int StateInProgress = 0;

    // cached briefly: this runs on the 1s download scan and each call opens several SQLite files
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
            if ((DateTime.UtcNow - _cacheAt).TotalSeconds < 2) return _cache;

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
    public static long TotalFor(string partialPath)
    {
        if (string.IsNullOrEmpty(partialPath)) return 0;
        string target = Path.GetFileName(partialPath);
        foreach (var r in InProgress())
        {
            string f = Path.GetFileName(r.File);
            if (f.Length == 0) continue;
            if (f.Equals(target, StringComparison.OrdinalIgnoreCase)) return r.Total; // current_path IS the .crdownload
        }
        return 0;
    }

    // The clean final filename for a partial file whose own name is useless ("Unconfirmed 12345.crdownload").
    public static string? NameFor(string partialPath)
    {
        if (string.IsNullOrEmpty(partialPath)) return null;
        string target = Path.GetFileName(partialPath);
        foreach (var r in InProgress())
            if (Path.GetFileName(r.File).Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                // target_path holds the final name; current_path is the partial. We only stored File
                // (=target_path), so strip a partial suffix if it somehow carries one.
                string n = Path.GetFileName(r.File);
                return PartialFiles.IsPartial(n, out string clean) && clean.Length > 0 ? clean : n;
            }
        return null;
    }

    private static void ReadInto(string dbPath, List<Row> rows)
    {
        // immutable=1 promises the file won't change under us, which is what lets SQLite skip the locking
        // protocol and read a database another process has open. Slightly stale data is fine here.
        string uri = "file:///" + dbPath.Replace('\\', '/').Replace(" ", "%20") + "?immutable=1";
        if (sqlite3_open_v2(Utf8(uri), out IntPtr db, OpenReadonly | OpenUri, IntPtr.Zero) != 0) { sqlite3_close(db); return; }
        try
        {
            const string sql = "SELECT target_path, current_path, received_bytes, total_bytes FROM downloads WHERE state = 0";
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out IntPtr st, IntPtr.Zero) != 0) return;
            try
            {
                while (sqlite3_step(st) == RowResult)
                {
                    string target = Str(sqlite3_column_text(st, 0));
                    string current = Str(sqlite3_column_text(st, 1));
                    long got = sqlite3_column_int64(st, 2), total = sqlite3_column_int64(st, 3);
                    // key on the partial file we can actually see on disk, falling back to the final name
                    rows.Add(new Row(current.Length > 0 ? current : target, got, total));
                }
            }
            finally { sqlite3_finalize(st); }
        }
        finally { sqlite3_close(db); }
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
