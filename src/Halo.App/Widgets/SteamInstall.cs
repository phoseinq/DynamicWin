using System;
using System.Collections.Generic;
using System.IO;

namespace Halo.Widgets;

internal static class SteamInstall
{
    private const long MinBytes = 1024 * 1024;
    private const int StaleSeconds = 90;

    internal readonly record struct Item(string Name, long Done, long Total);

    private static readonly object _lock = new();
    private static string[]? _libs;
    private static DateTime _libsAt = DateTime.MinValue;

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

    private static bool Kv(string line, out string key, out string value)
    {
        key = value = "";
        int k0 = line.IndexOf('"');
        if (k0 < 0) return false;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return false;
        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return false;
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return false;
        key = line.Substring(k0 + 1, k1 - k0 - 1);
        value = line.Substring(v0 + 1, v1 - v0 - 1);
        return true;
    }

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

            return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p!.Replace('/', '\\'));
        }
        catch { return null; }
    }
}
