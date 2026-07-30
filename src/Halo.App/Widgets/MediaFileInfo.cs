using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Halo.Widgets;

internal static class MediaFileInfo
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, long?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] VideoExt =
        { ".mkv", ".mp4", ".avi", ".mov", ".webm", ".m4v", ".flv", ".wmv", ".mpg", ".mpeg", ".ts", ".ogv" };

        public static long? Size(string? title, Action? onFound = null)
    {
        if (string.IsNullOrWhiteSpace(title) || !LooksLikeFile(title)) return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(title, out var known)) return known;
            if (!_inFlight.Add(title)) return null;
        }
        _ = Task.Run(() =>
        {
            long? found = null;
            try { found = Lookup(title!); } catch { }
            lock (_lock) { _cache[title!] = found; _inFlight.Remove(title!); }
            if (found is not null) onFound?.Invoke();
        });
        return null;
    }

        public static string Human(long bytes)
    {
        if (bytes <= 0) return "";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1d) return gb.ToString(gb >= 10d ? "0" : "0.#",
            System.Globalization.CultureInfo.InvariantCulture) + " GB";
        double mb = bytes / 1024d / 1024d;
        if (mb >= 1d) return mb.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        return (bytes / 1024d).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";
    }

    internal static bool LooksLikeFile(string title)
    {
        var t = title.ToLowerInvariant();
        foreach (var e in VideoExt) if (t.EndsWith(e, StringComparison.Ordinal)) return true;
        return false;
    }

    private static long? Lookup(string title)
    {
        var recent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Recent");
        if (!Directory.Exists(recent)) return null;

        var exact = Path.Combine(recent, title + ".lnk");
        if (File.Exists(exact) && Verify(exact, title) is { } hit) return hit;

        foreach (var lnk in Directory.EnumerateFiles(recent, "*.lnk"))
        {
            if (!Path.GetFileName(lnk).StartsWith(Path.GetFileNameWithoutExtension(title),
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (Verify(lnk, title) is { } size) return size;
        }
        return null;
    }

    private static long? Verify(string lnk, string title)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(lnk); } catch { return null; }
        if (bytes.Length is 0 or > 1_000_000) return null;

        foreach (var cand in Paths(bytes))
        {
            if (!string.Equals(Path.GetFileName(cand), title, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var fi = new FileInfo(cand);
                if (fi.Exists && fi.Length > 0) return fi.Length;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> Paths(byte[] bytes)
    {
        foreach (var s in new[] { Encoding.Latin1.GetString(bytes), Encoding.Unicode.GetString(bytes) })
        {
            for (int i = 0; i + 3 < s.Length; i++)
            {
                if (s[i + 1] != ':' || s[i + 2] != '\\') continue;
                char d = s[i];
                if (!char.IsLetter(d)) continue;
                int end = i + 3;
                while (end < s.Length && !char.IsControl(s[end]) && s[end] != '\0') end++;
                if (end - i > 6) yield return s.Substring(i, end - i);
                i = end;
            }
        }
    }
}
