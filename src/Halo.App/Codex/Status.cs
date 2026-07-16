using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Halo.Codex;

internal enum CodexSurface { Cli, Desktop }

internal sealed record CodexLimit(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);

internal sealed record CodexSnapshot(
    CodexSurface Source, string State, string? CurrentTool, DateTimeOffset? StartedAt,
    DateTimeOffset? CompactedAt, string? Message, string? Cwd, int Pid, int ConsolePid,
    long ContextUsed, long ContextMax, long PromptTokens, CodexLimit? PrimaryLimit,
    CodexLimit? SecondaryLimit, DateTimeOffset UpdatedAt, bool ProcessAlive);

internal static class CodexRollout
{
    internal static CodexSnapshot? Parse(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var state = "idle";
            string? currentTool = null;
            DateTimeOffset? startedAt = null;
            DateTimeOffset? compactedAt = null;
            string? message = null;
            string? cwd = null;
            var source = CodexSurface.Cli;
            long contextUsed = 0;
            long contextMax = 0;
            long promptTokens = 0;
            CodexLimit? primaryLimit = null;
            CodexLimit? secondaryLimit = null;
            var updatedAt = DateTimeOffset.MinValue;
            var sawEvent = false;

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var payload = Property(root, "payload") ?? root;
                    var eventType = String(payload, "type") ?? String(root, "type");
                    var timestamp = Timestamp(root, "timestamp");

                    sawEvent = true;
                    if (timestamp is { } value && value > updatedAt)
                        updatedAt = value;

                    switch (eventType)
                    {
                        case "session_meta":
                            cwd ??= String(payload, "cwd");
                            if (String(payload, "originator")?.Contains("Desktop", StringComparison.OrdinalIgnoreCase) == true)
                                source = CodexSurface.Desktop;
                            break;
                        case "task_started":
                            state = "working";
                            startedAt = timestamp;
                            contextMax = Number(payload, "model_context_window") ?? contextMax;
                            break;
                        case "custom_tool_call":
                            state = "working";
                            currentTool = ShortTool(String(payload, "name") ?? String(payload, "tool_name") ?? String(payload, "tool"));
                            break;
                        case "request_user_input":
                        case "request_user_approval":
                        case "approval":
                            state = "waiting_input";
                            message = String(payload, "message") ?? String(payload, "text") ?? String(payload, "prompt") ?? message;
                            break;
                        case "task_complete":
                            state = "idle";
                            currentTool = null;
                            startedAt = null;
                            break;
                        case "pre_compact":
                        case "precompact":
                        case "PreCompact":
                            state = "compacting";
                            break;
                        case "post_compact":
                        case "postcompact":
                        case "PostCompact":
                            state = "working";
                            compactedAt = timestamp;
                            break;
                        case "token_count":
                            var info = Property(payload, "info");
                            if (info is { } tokenInfo)
                            {
                                contextMax = Number(tokenInfo, "model_context_window") ?? contextMax;
                                contextUsed = TotalTokens(Property(tokenInfo, "total_token_usage")) ?? contextUsed;
                                promptTokens = TotalTokens(Property(tokenInfo, "last_token_usage")) ?? promptTokens;
                            }

                            var limits = Property(payload, "rate_limits");
                            if (limits is { } rateLimits)
                            {
                                primaryLimit = Limit(Property(rateLimits, "primary"), timestamp) ?? primaryLimit;
                                secondaryLimit = Limit(Property(rateLimits, "secondary"), timestamp) ?? secondaryLimit;
                            }
                            break;
                    }
                }
                catch (JsonException)
                {
                    // A rollout can be observed while its final line is being written.
                }
            }

            if (!sawEvent)
                return null;

            if (updatedAt == DateTimeOffset.MinValue)
                updatedAt = File.GetLastWriteTimeUtc(path);

            return new CodexSnapshot(
                source, state, currentTool, startedAt, compactedAt, message, cwd, 0, 0,
                contextUsed, contextMax, promptTokens, primaryLimit, secondaryLimit, updatedAt, false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CodexLimit? Limit(JsonElement? value, DateTimeOffset? timestamp)
    {
        if (value is not { } limit)
            return null;

        var usedPercent = NumberDouble(limit, "used_percent");
        var windowMinutes = Number(limit, "window_minutes");
        if (usedPercent is null || windowMinutes is null)
            return null;

        var resetsAt = Timestamp(limit, "resets_at");
        if (resetsAt is null && Number(limit, "resets_in_seconds") is { } seconds)
            resetsAt = (timestamp ?? DateTimeOffset.UtcNow).AddSeconds(seconds);

        return new CodexLimit(usedPercent.Value, checked((int)windowMinutes.Value), resetsAt);
    }

    private static long? TotalTokens(JsonElement? usage) => usage is { } value ? Number(value, "total_tokens") : null;

    private static string? ShortTool(string? tool) => tool?.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? String(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        Property(element, name) is { } value && value.TryGetInt64(out var number) ? number :
        Property(element, name) is { ValueKind: JsonValueKind.String } text && long.TryParse(text.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;

    private static double? NumberDouble(JsonElement element, string name) =>
        Property(element, name) is { } value && value.TryGetDouble(out var number) ? number :
        Property(element, name) is { ValueKind: JsonValueKind.String } text && double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;

    private static DateTimeOffset? Timestamp(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value is { ValueKind: JsonValueKind.Number } && value.Value.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix);

        if (value is { ValueKind: JsonValueKind.String } && DateTimeOffset.TryParse(value.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            return timestamp;

        return null;
    }
}

internal sealed class CodexStatusStore : IDisposable
{
    private const int ReloadDelayMilliseconds = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly string _statusDirectory;
    private readonly string _sessionsDirectory;
    private readonly Func<int, bool> _processAlive;
    private readonly object _reloadGate = new();
    private readonly object _scheduleGate = new();
    private readonly Timer? _reloadTimer;
    private readonly FileSystemWatcher? _statusWatcher;
    private readonly FileSystemWatcher? _rolloutWatcher;
    private CodexSnapshot? _desktopStatus;
    private CodexSnapshot? _cliStatus;
    private CodexSnapshot? _desktopRollout;
    private CodexSnapshot? _cliRollout;
    private CodexSnapshot? _current;
    private int _version;
    private volatile bool _disposed;

    internal CodexSnapshot? Current
    {
        get { lock (_reloadGate) return _current; }
    }

    internal int Version
    {
        get { lock (_reloadGate) return _version; }
    }

    internal CodexStatusStore()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "notch"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"),
            IsProcessAlive,
            watchFiles: true)
    {
    }

    internal CodexStatusStore(
        string statusDirectory,
        string sessionsDirectory,
        Func<int, bool> processAlive,
        bool watchFiles)
    {
        _statusDirectory = statusDirectory;
        _sessionsDirectory = sessionsDirectory;
        _processAlive = processAlive;
        Directory.CreateDirectory(_statusDirectory);
        Directory.CreateDirectory(_sessionsDirectory);

        if (watchFiles)
        {
            _reloadTimer = new Timer(_ => Reload(retryTransientReads: true), null, Timeout.Infinite, Timeout.Infinite);
            _statusWatcher = CreateWatcher(_statusDirectory, "*.json", includeSubdirectories: false);
            _rolloutWatcher = CreateWatcher(_sessionsDirectory, "*.jsonl", includeSubdirectories: true);
        }

        Reload(retryTransientReads: true);
    }

    internal void ForceRefresh() => Reload(retryTransientReads: true);

    internal static CodexSnapshot? Select(CodexSnapshot? desktop, CodexSnapshot? cli, DateTimeOffset now) =>
        IsActive(desktop, now) ? desktop : IsActive(cli, now) ? cli : null;

    public void Dispose()
    {
        lock (_scheduleGate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _statusWatcher?.Dispose();
            _rolloutWatcher?.Dispose();
            _reloadTimer?.Dispose();
        }

        lock (_reloadGate)
        {
        }
    }

    private FileSystemWatcher CreateWatcher(string directory, string filter, bool includeSubdirectories)
    {
        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = includeSubdirectories,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => ScheduleReload();
        watcher.Created += (_, _) => ScheduleReload();
        watcher.Deleted += (_, _) => ScheduleReload();
        watcher.Renamed += (_, _) => ScheduleReload();
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ScheduleReload()
    {
        lock (_scheduleGate)
        {
            if (_disposed)
                return;

            _reloadTimer?.Change(ReloadDelayMilliseconds, Timeout.Infinite);
        }
    }

    private void Reload(bool retryTransientReads)
    {
        lock (_reloadGate)
        {
            if (_disposed)
                return;

            ApplyStatusRead(ref _desktopStatus, ReadStatus(Path.Combine(_statusDirectory, "desktop.json"), CodexSurface.Desktop), retryTransientReads);
            ApplyStatusRead(ref _cliStatus, ReadStatus(Path.Combine(_statusDirectory, "cli.json"), CodexSurface.Cli), retryTransientReads);
            ScanRollouts(out var desktopRollout, out var cliRollout);
            _desktopRollout = desktopRollout ?? _desktopRollout;
            _cliRollout = cliRollout ?? _cliRollout;

            if (_disposed)
                return;

            var now = DateTimeOffset.UtcNow;
            _current = Select(
                Merge(_desktopStatus, _desktopRollout, now),
                Merge(_cliStatus, _cliRollout, now),
                now);
            _version++;
        }
    }

    private void ApplyStatusRead(ref CodexSnapshot? target, StatusRead read, bool retryTransientReads)
    {
        if (retryTransientReads &&
            (read.Kind == StatusReadKind.Transient || read.Kind == StatusReadKind.Missing && target is not null))
        {
            Thread.Sleep(ReloadDelayMilliseconds);
            read = ReadStatus(read.Path, read.Source);
        }

        if (read.Kind == StatusReadKind.Success)
            target = read.Snapshot;
        else if (read.Kind == StatusReadKind.Missing)
            target = null;
    }

    private static bool IsActive(CodexSnapshot? snapshot, DateTimeOffset now) =>
        snapshot is not null && snapshot.State != "ended" &&
        (snapshot.ProcessAlive || snapshot.UpdatedAt >= now.AddSeconds(-30));

    private static bool IsFresh(CodexSnapshot snapshot, DateTimeOffset now) =>
        snapshot.ProcessAlive || snapshot.UpdatedAt >= now.AddSeconds(-30);

    private static CodexSnapshot? Merge(CodexSnapshot? hook, CodexSnapshot? rollout, DateTimeOffset now)
    {
        if (hook is null)
            return rollout;
        if (rollout is null)
            return hook;

        var lifecycle = IsFresh(hook, now) ? hook : rollout;
        var hasRolloutUsage = rollout.ContextMax > 0 || rollout.PrimaryLimit is not null || rollout.SecondaryLimit is not null;
        return lifecycle with
        {
            Source = hook.Source,
            Cwd = lifecycle.Cwd ?? hook.Cwd ?? rollout.Cwd,
            Pid = hook.Pid,
            ConsolePid = hook.ConsolePid,
            ContextUsed = hasRolloutUsage ? rollout.ContextUsed : hook.ContextUsed,
            ContextMax = hasRolloutUsage ? rollout.ContextMax : hook.ContextMax,
            PromptTokens = hasRolloutUsage ? rollout.PromptTokens : hook.PromptTokens,
            PrimaryLimit = rollout.PrimaryLimit ?? hook.PrimaryLimit,
            SecondaryLimit = rollout.SecondaryLimit ?? hook.SecondaryLimit,
            UpdatedAt = hook.UpdatedAt > rollout.UpdatedAt ? hook.UpdatedAt : rollout.UpdatedAt,
            ProcessAlive = hook.ProcessAlive,
        };
    }

    private StatusRead ReadStatus(string path, CodexSurface source)
    {
        try
        {
            if (!File.Exists(path))
                return new StatusRead(path, source, StatusReadKind.Missing, null);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var status = JsonSerializer.Deserialize<HookStatus>(stream, JsonOptions);
            if (status is null)
                return new StatusRead(path, source, StatusReadKind.Transient, null);

            var updatedAt = status.UpdatedAt ?? File.GetLastWriteTimeUtc(path);
            var snapshot = new CodexSnapshot(
                source, status.State ?? "idle", status.CurrentTool, status.StartedAt, status.CompactedAt,
                status.Message, status.Cwd, status.Pid, status.ConsolePid, status.ContextUsed, status.ContextMax,
                status.PromptTokens, status.PrimaryLimit, status.SecondaryLimit, updatedAt,
                ProcessAlive(status.Pid));
            return new StatusRead(path, source, StatusReadKind.Success, snapshot);
        }
        catch (IOException)
        {
            return new StatusRead(path, source, StatusReadKind.Transient, null);
        }
        catch (JsonException)
        {
            return new StatusRead(path, source, StatusReadKind.Transient, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new StatusRead(path, source, StatusReadKind.Transient, null);
        }
    }

    private bool ProcessAlive(int pid)
    {
        try
        {
            return pid > 0 && _processAlive(pid);
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

    private void ScanRollouts(out CodexSnapshot? desktop, out CodexSnapshot? cli)
    {
        desktop = null;
        cli = null;

        try
        {
            foreach (var path in Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var snapshot = CodexRollout.Parse(path);
                if (snapshot?.Source == CodexSurface.Desktop && desktop is null)
                    desktop = snapshot;
                else if (snapshot?.Source == CodexSurface.Cli && cli is null)
                    cli = snapshot;

                if (desktop is not null && cli is not null)
                    break;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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

    private sealed class HookStatus
    {
        public string? State { get; set; }
        public string? CurrentTool { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompactedAt { get; set; }
        public string? Message { get; set; }
        public string? Cwd { get; set; }
        public int Pid { get; set; }
        public int ConsolePid { get; set; }
        public long ContextUsed { get; set; }
        public long ContextMax { get; set; }
        public long PromptTokens { get; set; }
        public CodexLimit? PrimaryLimit { get; set; }
        public CodexLimit? SecondaryLimit { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private enum StatusReadKind { Missing, Success, Transient }

    private readonly record struct StatusRead(
        string Path,
        CodexSurface Source,
        StatusReadKind Kind,
        CodexSnapshot? Snapshot);
}
