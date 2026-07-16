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
    private readonly Func<int, DateTimeOffset?> _processStartedAt;
    private readonly Func<DateTimeOffset> _clock;
    private readonly FileSystemWatcher? _watcher;

    public CcStatus? Current { get; private set; }
    public int Version { get; private set; }
    public bool IsLive => IsLiveStatus(Current, _processStartedAt, _clock());

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch");

    public StatusStore()
        : this(Path.Combine(Directory, "status.json"), GetProcessStartedAt, watchFiles: true)
    {
    }

    internal StatusStore(string path, Func<int, DateTimeOffset?> processStartedAt, bool watchFiles,
        Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _processStartedAt = processStartedAt;
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
