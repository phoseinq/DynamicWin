using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace Halo.Widgets;

// Resolves a media session's SourceAppUserModelId to the real app icon by matching a running
// process and extracting its executable icon. Cached; missed lookups retry every few seconds.
internal static class AppIcon
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, Bitmap> _ok = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, long> _missed = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? ForAumid(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        lock (_lock)
        {
            if (_ok.TryGetValue(aumid, out var cached)) return cached;
            if (_missed.TryGetValue(aumid, out var t) && Environment.TickCount64 - t < 3000) return null;
            var bmp = Resolve(aumid);
            if (bmp != null) _ok[aumid] = bmp; else _missed[aumid] = Environment.TickCount64;
            return bmp;
        }
    }

    private static Bitmap? Resolve(string aumid)
    {
        try
        {
            string? exe = ExeFromAumid(aumid);
            if (exe == null || !File.Exists(exe)) return null;
            using var ico = Icon.ExtractAssociatedIcon(exe);
            return ico?.ToBitmap();
        }
        catch { return null; }
    }

    private static string? ExeFromAumid(string aumid)
    {
        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(aumid)) return aumid;
        string key = Path.GetFileNameWithoutExtension(aumid); // "Chrome", "Spotify", ...
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string pn = p.ProcessName;
                if (pn.Length > 1 &&
                    (aumid.Contains(pn, StringComparison.OrdinalIgnoreCase) || pn.Contains(key, StringComparison.OrdinalIgnoreCase)))
                {
                    var f = p.MainModule?.FileName;
                    if (f != null) return f;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }
}
