using System;
using System.IO;
using System.Threading;

namespace Halo.Settings;

// The pill's view of settings.json, kept current while the panel is open.
//
// Watched rather than read once, because the panel is a SEPARATE process: the alternative is applying a
// change by restarting Halo, which is what the previous design did and what forced the greeting to be
// keyed on the app version so that a restart would not replay it. A watcher plus the same 1s poll
// StatusStore uses (FileSystemWatcher drops events under bursty writes) means a toggle in the panel is
// on the pill within a frame, and nothing restarts.
//
// Version is the same idiom as every other store here: bump it and the widgets re-render.
internal sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _poll;
    private readonly object _gate = new();
    private SettingsFile _current;
    private int _version;
    private bool _disposed;

    internal SettingsStore(string? path = null, bool watch = true)
    {
        _path = path ?? SettingsFile.DefaultPath;
        _current = SettingsFile.Read(_path);
        if (!watch) return;
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Deleted += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _poll = new Timer(_ => Reload(), null, 1000, 1000);
        }
        catch { }   // a machine that will not give us a watcher still gets the settings it launched with
    }

    internal SettingsFile Current
    {
        get { lock (_gate) return _current; }
    }

    internal int Version => Volatile.Read(ref _version);

    internal event Action<SettingsFile>? Changed;

    internal bool Enabled(FeatureId id) => Current.Bool(SettingsKeys.Feature(id), true);

    // Writing is the panel's job, not the pill's - this exists for the one case where the pill itself
    // changes a setting (dragging the pill parks a new position, say), and it goes through the same file
    // so the panel sees it too.
    internal bool Set(string key, string value)
    {
        SettingsFile next;
        lock (_gate)
        {
            if (_current.Text(key, "") == value) return false;
            next = _current.With(key, value);
            if (!next.Save(_path)) return false;
            _current = next;
        }
        Interlocked.Increment(ref _version);
        Changed?.Invoke(next);
        return true;
    }

    private void Reload()
    {
        var next = SettingsFile.Read(_path);
        lock (_gate)
        {
            if (Same(_current, next)) return;
            _current = next;
        }
        Interlocked.Increment(ref _version);
        try { Changed?.Invoke(next); } catch { }
    }

    // A watcher fires several times for one save, and the 1s poll fires whether or not anything moved.
    // Comparing the maps keeps that from bumping Version - which is what widgets re-render on.
    private static bool Same(SettingsFile a, SettingsFile b)
    {
        if (a.Values.Count != b.Values.Count) return false;
        foreach (var (key, value) in a.Values)
            if (!b.Values.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
                return false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher?.Dispose(); } catch { }
        try { _poll?.Dispose(); } catch { }
    }
}
