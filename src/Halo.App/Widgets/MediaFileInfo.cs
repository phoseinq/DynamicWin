using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Halo.Widgets;

// How big is the thing that is playing? SMTC does not say. It hands over a title, an artist and a thumbnail
// and nothing about the file — there is no path anywhere in the API, which is why the panel could never show
// a size.
//
// The shell knows, though. Anything opened from Explorer leaves a shortcut in Recent, named after the file
// it points at, and a shortcut carries the target path. So: match the title against Recent, pull the path out
// of the .lnk, and — this is the part that makes a crude parse safe — only believe it if the file is really
// there and its name is really the one we were looking for. A wrong answer here would be a fabricated number
// on the pill, so the check is the point, not the extraction.
//
// Everything is cached, misses included, and the lookup runs off the render path.
internal static class MediaFileInfo
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, long?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] VideoExt =
        { ".mkv", ".mp4", ".avi", ".mov", ".webm", ".m4v", ".flv", ".wmv", ".mpg", ".mpeg", ".ts", ".ogv" };

    /// <summary>
    /// The file's size in bytes, or null while unknown. Never blocks: the first call for a title starts the
    /// lookup and returns null, and <paramref name="onFound"/> fires if it turns into an answer.
    /// </summary>
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

    /// <summary>"1.4 GB", "780 MB" — the units people say out loud, not the ones that are technically true.</summary>
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

        // the shortcut is named after the file, so the exact name is one probe; the scan is the fallback for
        // shells that decorate the name
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

    // Pull drive-letter paths out of the shortcut and keep the one that is actually the file we are asking
    // about. The .lnk binary format holds the target twice, once as ansi and once as utf-16, so both are
    // scanned; parsing the structure properly would be more code for the same answer, and File.Exists plus a
    // name match is a stronger check than a correct parse would be on its own.
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
