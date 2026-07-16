using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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
                CodexSurface.Cli, state, currentTool, startedAt, compactedAt, message, cwd, 0, 0,
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly string _directory;
    private readonly FileSystemWatcher _watcher;

    internal CodexSnapshot? Current { get; private set; }
    internal int Version { get; private set; }

    internal CodexStatusStore()
    {
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "notch");
        Directory.CreateDirectory(_directory);
        _watcher = new FileSystemWatcher(_directory, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Load();
        _watcher.Created += (_, _) => Load();
        _watcher.Deleted += (_, _) => Load();
        _watcher.Renamed += (_, _) => Load();
        Load();
    }

    internal void ForceRefresh() => Load();

    internal static CodexSnapshot? Select(CodexSnapshot? desktop, CodexSnapshot? cli, DateTimeOffset now) =>
        IsActive(desktop, now) ? desktop : IsActive(cli, now) ? cli : null;

    public void Dispose() => _watcher.Dispose();

    private void Load()
    {
        var desktop = Read(Path.Combine(_directory, "desktop.json"), CodexSurface.Desktop);
        var cli = Read(Path.Combine(_directory, "cli.json"), CodexSurface.Cli);
        Current = Select(desktop, cli, DateTimeOffset.UtcNow);
        Version++;
    }

    private static bool IsActive(CodexSnapshot? snapshot, DateTimeOffset now) =>
        snapshot is not null && snapshot.State != "ended" &&
        (snapshot.ProcessAlive || snapshot.UpdatedAt >= now.AddSeconds(-30));

    private static CodexSnapshot? Read(string path, CodexSurface source)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var status = JsonSerializer.Deserialize<HookStatus>(stream, JsonOptions);
            if (status is null)
                return null;

            var updatedAt = status.UpdatedAt ?? File.GetLastWriteTimeUtc(path);
            return new CodexSnapshot(
                source, status.State ?? "idle", status.CurrentTool, status.StartedAt, status.CompactedAt,
                status.Message, status.Cwd, status.Pid, status.ConsolePid, status.ContextUsed, status.ContextMax,
                status.PromptTokens, status.PrimaryLimit, status.SecondaryLimit, updatedAt,
                status.ProcessAlive ?? IsProcessAlive(status.Pid));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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
        public bool? ProcessAlive { get; set; }
    }
}
