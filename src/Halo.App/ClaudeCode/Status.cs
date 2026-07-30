using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Halo.ClaudeCode;

internal sealed class CcSession
{
    public long ContextUsed { get; set; }
    public long ContextMax { get; set; } = 200000;
    public long PromptTokens { get; set; } // tokens used by the currently running turn (not the total)
}

internal sealed class CcUsage
{
    public double FiveHourPct { get; set; }
    public string? FiveHourResetsAt { get; set; }
    public double WeeklyPct { get; set; }
    public string? WeeklyResetsAt { get; set; }
}

internal sealed class CcStatus
{
    public string? Name { get; set; } // generic-agent files: display name ("Gemini CLI"...)
    public string? Icon { get; set; } // generic-agent files: path to a png/ico for the circle
    public string? State { get; set; }
    public string? Cwd { get; set; }
    public int Pid { get; set; }
    public int ConsolePid { get; set; }
    public string? CurrentTool { get; set; }
    public string? ToolTarget { get; set; } // what the tool is acting on: a file, a program, a host
    public string? LastPrompt { get; set; }
    public string? StartedAt { get; set; }
    public string? Message { get; set; } // what Claude is asking (notify hook)
    public string? CompactedAt { get; set; } // when the last compact finished
    public long LastCompactMs { get; set; } // how long the last compact took (drives the % estimate)
    public CcSession? Session { get; set; }
    public CcUsage? Usage { get; set; }
    public string? UpdatedAt { get; set; }
}

internal sealed class StatusStore
{
    private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public const int MaxSessions = 4;

    private readonly string _path;
    private readonly string? _appPath; // the Claude desktop app's surface (CLI takes priority)
    private readonly Func<int, DateTimeOffset?> _processStartedAt;
    private readonly Func<DateTimeOffset> _clock;
    private readonly FileSystemWatcher? _watcher;

    private CcStatus? _cli, _app;
    // multi-session: every live status*.json/app.json is pinned to a stable slot (one widget each)
    private Dictionary<string, CcStatus> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string?[] _slotPaths = new string?[MaxSessions];
    private readonly (CcStatus? live, DateTimeOffset at, int version)[] _slotCache
        = new (CcStatus?, DateTimeOffset, int)[MaxSessions];
    private CcStatus? _selected;
    private DateTimeOffset _selectedAt = DateTimeOffset.MinValue;
    private int _selectedVersion = -1;

    // CLI when it's live, else the desktop app — liveness re-checked at most once a second
    public CcStatus? Current
    {
        get
        {
            var now = _clock();
            if (_selectedVersion != Version || now - _selectedAt > TimeSpan.FromSeconds(1))
            {
                _selected = IsLiveStatus(_cli, _processStartedAt, now) ? _cli
                    : IsLiveStatus(_app, _processStartedAt, now) ? _app
                    : _cli ?? _app;
                _selectedAt = now;
                _selectedVersion = Version;
            }
            return _selected;
        }
    }

    public int Version { get; private set; }

    // liveness re-checked at most once a second (process lookups per frame add up)
    private bool _live;
    private DateTimeOffset _liveAt = DateTimeOffset.MinValue;
    private int _liveVersion = -1;

    public bool IsLive
    {
        get
        {
            var now = _clock();
            if (_liveVersion != Version || now - _liveAt > TimeSpan.FromSeconds(1))
            {
                _live = IsLiveStatus(Current, _processStartedAt, now);
                _liveAt = now;
                _liveVersion = Version;
            }
            return _live;
        }
    }

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch");

    public StatusStore()
        : this(Path.Combine(Directory, "status.json"), GetProcessStartedAt, watchFiles: true,
            appPath: Path.Combine(Directory, "app.json"))
    {
    }

    internal StatusStore(string path, Func<int, DateTimeOffset?> processStartedAt, bool watchFiles,
        Func<DateTimeOffset>? clock = null, string? appPath = null)
    {
        _path = path;
        _appPath = appPath;
        _processStartedAt = processStartedAt;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            System.IO.Directory.CreateDirectory(directory);
        Load();

        if (!watchFiles)
            return;

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Load();
        _watcher.Created += (_, _) => Load();
        _watcher.Deleted += (_, _) => Load();
        _watcher.Renamed += (_, _) => Load();

        // FileSystemWatcher silently drops events under bursty writes (a busy turn writes many status
        // updates/sec), leaving the widget frozen on a stale snapshot ("asking you" stuck after you
        // answered). A 1s poll is the safety net; the watcher stays the instant fast-path.
        _poll = new System.Threading.Timer(_ => Load(), null, 1000, 1000);
    }

    private readonly System.Threading.Timer? _poll;

    private static bool IsLiveStatus(CcStatus? status, Func<int, DateTimeOffset?> processStartedAt, DateTimeOffset now)
    {
        if (status is null)
            return false;

        if (!DateTimeOffset.TryParse(status.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var updatedAt))
            return false;

        if (status.Pid > 0)
        {
            var startedAt = ProcessStartTime(status.Pid, processStartedAt);
            return startedAt.HasValue && startedAt.Value <= updatedAt + ProcessStartTolerance;
        }

        if (status.Pid < 0)
            return false;

        if (status.State is not ("working" or "waiting" or "waiting_input" or "compacting"))
            return false;

        return updatedAt >= now.AddSeconds(-30);
    }

    private static DateTimeOffset? ProcessStartTime(int pid, Func<int, DateTimeOffset?> processStartedAt)
    {
        try
        {
            return processStartedAt(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static DateTimeOffset? GetProcessStartedAt(int pid)
    {
        if (pid <= 0)
            return null;

        using var process = Process.GetProcessById(pid);
        return process.HasExited ? null : new DateTimeOffset(process.StartTime);
    }

    private readonly object _loadGate = new(); // watcher thread + 1s poll can both fire Load()
    private void Load()
    {
        lock (_loadGate)
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            var pattern = Path.GetFileNameWithoutExtension(_path) + "*.json"; // status.json + status-{pid}.json
            var next = new Dictionary<string, CcStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in System.IO.Directory.EnumerateFiles(dir, pattern))
            {
                _files.TryGetValue(p, out var prev);
                if (Read(p, prev) is { } st) next[p] = st;
            }
            if (_appPath is not null)
            {
                _files.TryGetValue(_appPath, out var prev);
                _app = Read(_appPath, prev);
                if (_app is not null) next[_appPath] = _app;
            }
            _files = next;
            AssignSlots(_clock());
            // legacy single-session consumers: freshest live CLI status
            _cli = LiveFiles(_clock())
                .Where(kv => !string.Equals(kv.Key, _appPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Value.UpdatedAt, StringComparer.Ordinal)
                .Select(kv => kv.Value).FirstOrDefault();
            Version++;
        }
        catch
        {
        }
    }

    // live files, deduped by agent pid (a session migrating from legacy status.json to
    // status-{pid}.json briefly exists as both — keep the freshest)
    private IEnumerable<KeyValuePair<string, CcStatus>> LiveFiles(DateTimeOffset now) =>
        _files.Where(kv => IsLiveStatus(kv.Value, _processStartedAt, now))
            .GroupBy(kv => kv.Value.Pid > 0 ? kv.Value.Pid.ToString() : kv.Key)
            .Select(g => g.OrderByDescending(kv => kv.Value.UpdatedAt, StringComparer.Ordinal).First());

    // pin each live file to a slot; existing assignments keep their slot so widgets don't shuffle
    private void AssignSlots(DateTimeOffset now)
    {
        var live = LiveFiles(now).Select(kv => kv.Key).ToArray();
        for (int i = 0; i < _slotPaths.Length; i++)
            if (_slotPaths[i] is { } p && Array.FindIndex(live, x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) < 0)
                _slotPaths[i] = null;
        foreach (var p in live)
        {
            if (Array.FindIndex(_slotPaths, x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)) >= 0)
                continue;
            int free = Array.IndexOf(_slotPaths, null);
            if (free < 0) break; // ponytail: >4 concurrent sessions drop off; bump MaxSessions if it ever hurts
            _slotPaths[free] = p;
        }
    }

    // How many slots hold a live session. Only used to decide whether a session icon is worth numbering:
    // a lone session badged "1" says nothing, it just asks the user what the other ones are.
    public int LiveSessions()
    {
        int n = 0;
        for (int i = 0; i < MaxSessions; i++)
            if (SessionLive(i) is not null) n++;
        return n;
    }

    // the slot's status when its session is live, else null — cached 1s (hit per frame per widget)
    public CcStatus? SessionLive(int slot)
    {
        var now = _clock();
        ref var c = ref _slotCache[slot];
        if (c.version != Version || now - c.at > TimeSpan.FromSeconds(1))
        {
            var st = _slotPaths[slot] is { } p && _files.TryGetValue(p, out var s) ? s : null;
            c = (IsLiveStatus(st, _processStartedAt, now) ? st : null, now, Version);
        }
        return c.live;
    }

    // missing file = no session (null); a transient read/parse failure keeps the previous value
    private static CcStatus? Read(string path, CcStatus? previous)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<CcStatus>(fs, Opts) ?? previous;
        }
        catch
        {
            return previous;
        }
    }
}
