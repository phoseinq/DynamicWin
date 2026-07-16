using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    public string? State { get; set; }
    public string? Cwd { get; set; }
    public int Pid { get; set; }
    public int ConsolePid { get; set; }
    public string? CurrentTool { get; set; }
    public string? LastPrompt { get; set; }
    public string? StartedAt { get; set; }
    public string? Message { get; set; } // what Claude is asking (notify hook)
    public string? CompactedAt { get; set; } // when the last compact finished
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

    private readonly string _path;
    private readonly string? _appPath; // the Claude desktop app's surface (CLI takes priority)
    private readonly Func<int, DateTimeOffset?> _processStartedAt;
    private readonly Func<DateTimeOffset> _clock;
    private readonly FileSystemWatcher? _watcher;

    private CcStatus? _cli, _app;
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
    public bool IsLive => IsLiveStatus(Current, _processStartedAt, _clock());

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
    }

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

    private static DateTimeOffset? GetProcessStartedAt(int pid)
    {
        if (pid <= 0)
            return null;

        using var process = Process.GetProcessById(pid);
        return process.HasExited ? null : new DateTimeOffset(process.StartTime);
    }

    private void Load()
    {
        try
        {
            _cli = Read(_path, _cli);
            _app = _appPath is null ? null : Read(_appPath, _app);
            Version++;
        }
        catch
        {
        }
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
