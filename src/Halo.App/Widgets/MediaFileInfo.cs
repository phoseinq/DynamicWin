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
        var t = title.Trim();
        if (t.Length is < 6 or > 200 || t.IndexOfAny(new[] { '/', '\\' }) >= 0) return false;
        if (HasVideoExt(t)) return true;
        // Windows' Media Player hands the name over WITHOUT its extension - the shortcut on disk is
        // "Spy.2015.1080p.BluRay.Farsi.Dubbed.Film2Media.mkv.lnk" and the title is the same thing minus the
        // ".mkv", so requiring an extension meant the lookup never even started for the one player that
        // prompted it. A release name is recognisable without one: several dot-separated pieces.
        return t.Split('.', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
    }

    private static bool HasVideoExt(string name)
    {
        var t = name.ToLowerInvariant();
        foreach (var e in VideoExt) if (t.EndsWith(e, StringComparison.Ordinal)) return true;
        return false;
    }

    // the candidate is the file we are asking about if the names match, with or without the extension the
    // title may not have carried
    internal static bool SameFile(string candidatePath, string title)
    {
        var name = Path.GetFileName(candidatePath);
        if (string.Equals(name, title, StringComparison.OrdinalIgnoreCase)) return true;
        if (!HasVideoExt(name)) return false;
        var stem = name.Substring(0, name.LastIndexOf('.'));
        return string.Equals(stem, title, StringComparison.OrdinalIgnoreCase);
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

        // "Film2Media" is not an extension, so the prefix is the title itself when it carries no real one -
        // GetFileNameWithoutExtension would have cut the last dotted piece off and matched too loosely
        var prefix = HasVideoExt(title) ? title.Substring(0, title.LastIndexOf('.')) : title;
        foreach (var lnk in Directory.EnumerateFiles(recent, "*.lnk"))
        {
            if (!Path.GetFileName(lnk).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
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
            if (!SameFile(cand, title)) continue;
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
