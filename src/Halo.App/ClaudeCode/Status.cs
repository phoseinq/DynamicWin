using System;
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
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly string _path;
    private readonly Func<int, bool> _processAlive;
    private readonly Func<DateTimeOffset> _clock;
    private readonly FileSystemWatcher? _watcher;

    public CcStatus? Current { get; private set; }
    public int Version { get; private set; }
    public bool IsLive => IsLiveStatus(Current, _processAlive, _clock());

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch");

    public StatusStore()
        : this(Path.Combine(Directory, "status.json"), IsProcessAlive, watchFiles: true)
    {
    }

    internal StatusStore(string path, Func<int, bool> processAlive, bool watchFiles,
        Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _processAlive = processAlive;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            System.IO.Directory.CreateDirectory(directory);
        Load();

        if (!watchFiles)
            return;

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Load();
        _watcher.Created += (_, _) => Load();
        _watcher.Deleted += (_, _) => Load();
        _watcher.Renamed += (_, _) => Load();
    }

    private static bool IsLiveStatus(CcStatus? status, Func<int, bool> processAlive, DateTimeOffset now)
    {
        if (status is null)
            return false;

        if (status.Pid > 0)
            return ProcessIsAlive(status.Pid, processAlive);

        if (status.Pid < 0)
            return false;

        if (status.State is not ("working" or "waiting" or "waiting_input" or "compacting")
            || !DateTimeOffset.TryParse(status.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var updatedAt))
            return false;

        return updatedAt >= now.AddSeconds(-30);
    }

    private static bool ProcessIsAlive(int pid, Func<int, bool> processAlive)
    {
        try
        {
            return processAlive(pid);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Current = null;
                Version++;
                return;
            }
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Current = JsonSerializer.Deserialize<CcStatus>(fs, Opts);
            Version++;
        }
        catch
        {
        }
    }
}
