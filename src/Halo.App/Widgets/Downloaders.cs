using System;
using System.Collections.Generic;
using System.IO;

namespace Halo.Widgets;

internal static class Downloaders
{
    private const int MaxEntries = 24;
    private static readonly object _lock = new();
    private static readonly Dictionary<string, string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

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
                return;
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
                    if (dir.Length > 0 && Directory.Exists(dir)) _dirs[dir] = app;
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
