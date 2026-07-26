using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Halo.Widgets;

internal static class BrowserDownloads
{

    internal readonly record struct Row(string File, long Received, long Total, string Target);

    private const int OpenReadonly = 0x1, OpenUri = 0x40, RowResult = 100;
    private const double CacheSeconds = 2.5;
    private const double IdleMinutes = 30;

    private static readonly object _lock = new();
    private static List<Row> _cache = new();
    private static DateTime _cacheAt = DateTime.MinValue;

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

    public static List<Row> InProgress()
    {
        lock (_lock)
            if ((DateTime.UtcNow - _cacheAt).TotalSeconds < CacheSeconds) return _cache;

        var rows = new List<Row>();
        foreach (var db in HistoryFiles())
        {
            try { ReadInto(db, rows); }
            catch { }
        }
        lock (_lock) { _cache = rows; _cacheAt = DateTime.UtcNow; }
        return rows;
    }

    public static long TotalFor(string partialPath)
    {
        if (string.IsNullOrEmpty(partialPath)) return 0;
        string partial = Path.GetFileName(partialPath);
        PartialFiles.IsPartial(partial, out string clean);
        foreach (var r in InProgress())
        {
            if (Same(Path.GetFileName(r.File), partial, clean)) return r.Total;
            if (Same(Path.GetFileName(r.Target), partial, clean)) return r.Total;
        }
        return 0;
    }

    private static bool Same(string candidate, string partial, string clean)
    {
        if (candidate.Length == 0) return false;
        if (candidate.Equals(partial, StringComparison.OrdinalIgnoreCase)) return true;
        if (clean.Length > 0 && candidate.Equals(clean, StringComparison.OrdinalIgnoreCase)) return true;

        return clean.Length > 0 && StripCopySuffix(clean) is { Length: > 0 } bare
            && candidate.Equals(bare, StringComparison.OrdinalIgnoreCase);
    }

    internal static string StripCopySuffix(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || !stem.EndsWith(")", StringComparison.Ordinal)) return fileName;
        string inner = stem.Substring(open + 2, stem.Length - open - 3);
        if (inner.Length == 0 || inner.Length > 3) return fileName;
        foreach (char c in inner) if (c is < '0' or > '9') return fileName;
        return stem.Substring(0, open) + ext;
    }

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

    private static void ReadInto(string dbPath, List<Row> rows)
    {
        string wal = dbPath + "-wal";

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
