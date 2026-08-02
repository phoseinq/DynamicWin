using System;
using System.IO;
using System.Threading;

namespace Halo.Settings;

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
        catch { }
    }

    internal SettingsFile Current
    {
        get { lock (_gate) return _current; }
    }

    internal static SettingsStore? Shared { get; set; }

    internal static bool On(string key, bool fallback = true)
        => Shared?.Current.Bool(key, fallback) ?? fallback;

    internal static int Percent(string key, int fallback)
    {
        try
        {
            string text = (Shared?.Current.Text(key, "") ?? "").TrimEnd('%', ' ');
            return int.TryParse(text, out var value) && value is >= 0 and <= 100 ? value : fallback;
        }
        catch { return fallback; }
    }

    internal int Version => Volatile.Read(ref _version);

    internal event Action<SettingsFile>? Changed;

    internal bool Enabled(FeatureId id) => Current.Bool(SettingsKeys.Feature(id), true);

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
