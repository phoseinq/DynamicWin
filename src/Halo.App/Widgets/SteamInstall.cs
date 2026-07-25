using System;
using System.Collections.Generic;
using System.IO;

namespace Halo.Widgets;

// Steam never puts a percentage in its window title, so the title scanner in Downloads.cs cannot see it.
// It does keep honest numbers on disk: every app has a steamapps/appmanifest_<id>.acf with BytesDownloaded
// and BytesToDownload, updated as the download runs. Same shape as GameInstall's Xbox staging reader.
//
// BytesDownloaded < BytesToDownload is the primary signal because it is unambiguous. StateFlags is NOT
// used as a gate: its bit semantics were never verified against a live Steam download, and guessing them
// would risk either missing downloads or inventing them.
internal static class SteamInstall
{
    private const long MinBytes = 1024 * 1024;   // ignore tiny manifest churn
    private const int StaleSeconds = 90;         // manifest untouched this long → not actively downloading

    internal readonly record struct Item(string Name, long Done, long Total);

    private static readonly object _lock = new();
    private static string[]? _libs;
    private static DateTime _libsAt = DateTime.MinValue;

    // The active download, or null. Picks the manifest with the most bytes outstanding when several are
    // mid-update (Steam downloads one at a time, but a queue leaves several partial manifests behind).
    public static Item? Current()
    {
        try
        {
            Item? best = null;
            long bestOutstanding = 0;
            var now = DateTime.UtcNow;
            foreach (var lib in Libraries())
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                string[] files;
                try { files = Directory.GetFiles(apps, "appmanifest_*.acf"); } catch { continue; }
                foreach (var f in files)
                {
                    try { if ((now - File.GetLastWriteTimeUtc(f)).TotalSeconds > StaleSeconds) continue; }
                    catch { continue; }
                    if (!Parse(SafeRead(f), out var item)) continue;
                    long outstanding = item.Total - item.Done;
                    if (outstanding <= 0 || item.Total < MinBytes) continue;
                    if (best is null || outstanding > bestOutstanding) { best = item; bestOutstanding = outstanding; }
                }
            }
            return best;
        }
        catch { return null; }
    }

    // Steam holds the manifest open while it writes, so read with full sharing.
    private static string SafeRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    // A .acf is Valve's KeyValues text: "key"<tab>"value" lines inside nested braces. The four fields we
    // need are all at the top level, so a line scan is enough and cheaper than a real parser.
    internal static bool Parse(string text, out Item item)
    {
        item = default;
        if (string.IsNullOrEmpty(text)) return false;
        string name = "";
        long done = -1, total = -1;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != '"') continue;
            if (!Kv(line, out string key, out string val)) continue;
            switch (key)
            {
                case "name": if (name.Length == 0) name = val; break;
                case "BytesDownloaded": long.TryParse(val, out done); break;
                case "BytesToDownload": long.TryParse(val, out total); break;
            }
        }
        if (total <= 0 || done < 0) return false;
        item = new Item(name.Length > 0 ? name : "Steam game", done, Math.Max(done, total));
        return true;
    }

    // `"key"\t\t"value"` → key, value. Returns false for the brace-only and section-header lines.
    private static bool Kv(string line, out string key, out string value)
    {
        key = value = "";
        int k0 = line.IndexOf('"');
        if (k0 < 0) return false;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return false;
        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return false;                      // section header like "AppState" with no value
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return false;
        key = line.Substring(k0 + 1, k1 - k0 - 1);
        value = line.Substring(v0 + 1, v1 - v0 - 1);
        return true;
    }

    // Games live wherever the user put them: libraryfolders.vdf lists every library root (three on this
    // machine, across C:, H: and D:). Cached — it changes only when a library is added.
    private static string[] Libraries()
    {
        lock (_lock)
            if (_libs != null && (DateTime.UtcNow - _libsAt).TotalMinutes < 5) return _libs;

        var found = new List<string>();
        try
        {
            string? steam = SteamPath();
            if (steam != null)
            {
                found.Add(steam);
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (var p in ParseLibraries(SafeRead(vdf)))
                    {
                        bool dup = false;
                        foreach (var have in found)
                            if (string.Equals(have, p, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (!dup) found.Add(p);
                    }
            }
        }
        catch { }

        var arr = found.ToArray();
        lock (_lock) { _libs = arr; _libsAt = DateTime.UtcNow; }
        return arr;
    }

    // the "path" entries in libraryfolders.vdf, with VDF's doubled backslashes unescaped
    internal static List<string> ParseLibraries(string vdf)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(vdf)) return list;
        foreach (var raw in vdf.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Kv(line, out _, out string val)) continue;
            string path = val.Replace("\\\\", "\\");
            if (path.Length > 0) list.Add(path);
        }
        return list;
    }

    private static string? SteamPath()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            string? p = k?.GetValue("SteamPath") as string;
            // the registry stores it lowercase with forward slashes ("c:/program files (x86)/steam")
            return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p!.Replace('/', '\\'));
        }
        catch { return null; }
    }
}
