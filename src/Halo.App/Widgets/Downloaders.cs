using System;
using System.Collections.Generic;
using System.IO;

namespace Halo.Widgets;

// Learned download locations. Halo can't watch the whole filesystem, so PartialFiles starts at the user's
// Downloads folder; whenever it catches a partial file elsewhere it records (app, directory) here and that
// directory is watched from then on. The learning is about the DIRECTORY — that is what lets a launcher
// downloading into D:\Games\... show up at all. Same shape as BannerGate's learned-app set: a flat TSV
// under %LOCALAPPDATA%\Halo that survives restarts, is capped, and can simply be deleted.
internal static class Downloaders
{
    private const int MaxEntries = 24;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _dirs = new(StringComparer.OrdinalIgnoreCase); // dir → app
    private static bool _loaded;

    // Never learn from these: they write partial-looking files as a matter of course, and blaming them
    // would pin the pill to something that isn't a user download.
    private static readonly string[] Ignore =
        { "halo.app", "halo.hooks", "msiexec", "trustedinstaller", "wuauclt", "svchost", "explorer" };

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");
    private static string StatePath => Path.Combine(Dir, "downloaders.tsv");

    public static IEnumerable<string> Directories()
    {
        Load();
        lock (_lock) return new List<string>(_dirs.Keys);
    }

    // The app that owns a directory, for the pill's label/icon when the file itself gives no clue.
    public static string? AppFor(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return null;
        Load();
        lock (_lock) return _dirs.TryGetValue(directory!, out var app) ? app : null;
    }

    public static void Learn(int pid, string? directory)
    {
        if (pid == 0 || string.IsNullOrEmpty(directory)) return;
        string app;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); app = p.ProcessName; }
        catch { return; }
        foreach (var bad in Ignore)
            if (app.Equals(bad, StringComparison.OrdinalIgnoreCase)) return;

        Load();
        bool added;
        lock (_lock)
        {
            if (_dirs.TryGetValue(directory!, out var known) && known.Equals(app, StringComparison.OrdinalIgnoreCase))
                return;                                  // already known → no disk write per scan
            if (_dirs.Count >= MaxEntries && !_dirs.ContainsKey(directory!)) return;
            _dirs[directory!] = app;
            added = true;
        }
        if (added) Append(directory!, app);
    }

    private static void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(StatePath)) return;
                foreach (var line in File.ReadAllLines(StatePath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string dir = line.Substring(0, tab), app = line.Substring(tab + 1);
                    if (dir.Length > 0 && Directory.Exists(dir)) _dirs[dir] = app; // drop paths that are gone
                }
            }
            catch { }
        }
    }

    private static void Append(string directory, string app)
    {
        try { Directory.CreateDirectory(Dir); File.AppendAllText(StatePath, $"{directory}\t{app}\r\n"); }
        catch { }
    }
}
