using Halo.Codex;

namespace Halo.Tests;

public sealed class CodexRolloutTests
{
    [Fact]
    public void DesktopPresence_KeepsQuietRolloutIdleWhileAppRuns()
    {
        var now = DateTimeOffset.UtcNow;
        var rollout = Snapshot(CodexSurface.Desktop, now.AddMinutes(-2), alive: false);

        var value = CodexStatusStore.NormalizeDesktop(
            rollout, new CodexDesktopPresence(true, now.AddHours(-1)), now);

        Assert.Equal("idle", value!.State);
    }

    [Fact]
    public void DesktopPresence_DropsSnapshotWhenAppStops()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(CodexStatusStore.NormalizeDesktop(
            Snapshot(CodexSurface.Desktop, now, false), new(false, default), now));
    }

    [Fact]
    public void Parse_UsesLatestTokenCountAndTaskState()
    {
        var path = TempRollout(
            Event("task_started", "\"model_context_window\":353400"),
            TokenCount(total: 9_100, context: 200_000, primaryUsed: 12, primaryWindow: 300,
                primaryReset: 1784808000, secondaryUsed: 21, secondaryWindow: 10_080, secondaryResetInSeconds: 600),
            TokenCount(total: 18_420, context: 353_400, primaryUsed: 37, primaryWindow: 300, primaryReset: 1784808749),
            ToolCall("functions.exec"));

        var value = CodexRollout.Parse(path)!;

        Assert.Equal("working", value.State);
        Assert.Equal("exec", value.CurrentTool);
        Assert.Equal(18_420, value.ContextUsed);
        Assert.Equal(353_400, value.ContextMax);
        Assert.Equal(37, value.PrimaryLimit!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784808749), value.PrimaryLimit.ResetsAt);
        Assert.Equal(21, value.SecondaryLimit!.UsedPercent);
        Assert.Equal(10_080, value.SecondaryLimit.WindowMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-07-16T12:10:00Z"), value.SecondaryLimit.ResetsAt);
    }

    [Fact]
    public void Parse_WaitsForInputAndPreservesMessage()
    {
        var path = TempRollout(
            Event("task_started", "\"model_context_window\":353400"),
            Event("request_user_input", "\"message\":\"Choose a path\""));

        var value = CodexRollout.Parse(path)!;

        Assert.Equal("waiting_input", value.State);
        Assert.Equal("Choose a path", value.Message);
        Assert.NotNull(value.StartedAt);
    }

    [Fact]
    public void Parse_TaskCompletionClearsActiveTaskDetails()
    {
        var path = TempRollout(
            Event("task_started", "\"model_context_window\":353400"),
            ToolCall("functions.exec"),
            Event("task_complete", "\"completed_at\":1784808749"));

        var value = CodexRollout.Parse(path)!;

        Assert.Equal("idle", value.State);
        Assert.Null(value.CurrentTool);
        Assert.Null(value.StartedAt);
    }

    [Fact]
    public void Parse_IgnoresNumericNullFields()
    {
        var path = TempRollout(
            Event("task_started", "\"model_context_window\":null"),
            Event("token_count", "\"info\":{\"model_context_window\":null,\"total_token_usage\":{\"total_tokens\":null},\"last_token_usage\":{\"total_tokens\":null}},\"rate_limits\":{\"primary\":{\"used_percent\":null,\"window_minutes\":null,\"resets_at\":null},\"secondary\":null}"));

        var value = CodexRollout.Parse(path)!;

        Assert.Equal("working", value.State);
        Assert.Equal(CodexSnapshotFields.None, value.PresentFields);
        Assert.Null(value.PrimaryLimit);
        Assert.Null(value.SecondaryLimit);
    }

    [Fact]
    public void Select_PrefersActiveDesktopOverCli()
    {
        var now = DateTimeOffset.UtcNow;
        var cli = Snapshot(CodexSurface.Cli, now, alive: true);
        var desktop = Snapshot(CodexSurface.Desktop, now.AddSeconds(-2), alive: true);

        Assert.Same(desktop, CodexStatusStore.Select(desktop, cli, now));
    }

    [Fact]
    public void Select_FallsBackFromStaleDesktopToCli()
    {
        var now = DateTimeOffset.UtcNow;
        var desktop = Snapshot(CodexSurface.Desktop, now.AddMinutes(-10), alive: false);
        var cli = Snapshot(CodexSurface.Cli, now, alive: true);

        Assert.Same(cli, CodexStatusStore.Select(desktop, cli, now));
    }

    [Fact]
    public void Select_RejectsEndedDesktopEvenWhenProcessIsAlive()
    {
        var now = DateTimeOffset.UtcNow;
        var desktop = Snapshot(CodexSurface.Desktop, now, alive: true) with { State = "ended" };
        var cli = Snapshot(CodexSurface.Cli, now, alive: true);

        Assert.Same(cli, CodexStatusStore.Select(desktop, cli, now));
    }

    [Fact]
    public void ForceRefresh_MergesNewestRolloutUsageIntoHookState()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteStatus(temp.Status, "cli", "working", now, pid: 42, currentTool: "hook-tool");
        WriteRollout(temp.Sessions, "older", "codex_cli_rs", now.AddSeconds(-2),
            TokenCount(total: 100, context: 200_000, primaryUsed: 10, primaryWindow: 300, primaryReset: 1784808000));
        WriteRollout(temp.Sessions, "newer", "codex_cli_rs", now,
            TokenCount(total: 800, context: 353_400, primaryUsed: 35, primaryWindow: 300,
                primaryReset: 1784808749, secondaryUsed: 45, secondaryWindow: 10_080, secondaryResetInSeconds: 900));

        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => true, watchFiles: false,
            desktopPresence: StoppedDesktop);

        Assert.Equal(CodexSurface.Cli, store.Current!.Source);
        Assert.Equal("hook-tool", store.Current.CurrentTool);
        Assert.Equal(800, store.Current.ContextUsed);
        Assert.Equal(353_400, store.Current.ContextMax);
        Assert.Equal(35, store.Current.PrimaryLimit!.UsedPercent);
        Assert.Equal(45, store.Current.SecondaryLimit!.UsedPercent);

        WriteRollout(temp.Sessions, "latest", "codex_cli_rs", now.AddSeconds(1),
            TokenCount(total: 1_200, context: 400_000, primaryUsed: 51, primaryWindow: 300, primaryReset: 1784809000));
        store.ForceRefresh();

        Assert.Equal(1_200, store.Current!.ContextUsed);
        Assert.Equal(51, store.Current.PrimaryLimit!.UsedPercent);
    }

    [Fact]
    public void ForceRefresh_UsesActiveRolloutWhenHookIsStale()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteStatus(temp.Status, "desktop", "ended", now.AddMinutes(-5), pid: 0);
        WriteRollout(temp.Sessions, "desktop", "Codex Desktop", now,
            EventAt(now, "task_started", "\"model_context_window\":353400"));

        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => false, watchFiles: false,
            desktopPresence: RunningDesktop);

        Assert.Equal(CodexSurface.Desktop, store.Current!.Source);
        Assert.Equal("working", store.Current.State);
    }

    [Fact]
    public void ForceRefresh_RecomputesProcessLivenessAndIgnoresPersistedTrue()
    {
        using var temp = new TempDirectory();
        var alive = false;
        WriteStatus(temp.Status, "cli", "working", DateTimeOffset.UtcNow.AddMinutes(-5), pid: 424242, processAlive: true);

        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => alive, watchFiles: false,
            desktopPresence: StoppedDesktop);

        Assert.Null(store.Current);

        alive = true;
        store.ForceRefresh();

        Assert.NotNull(store.Current);
        Assert.True(store.Current!.ProcessAlive);
    }

    [Fact]
    public void ForceRefresh_RateLimitsWithoutInfoPreserveHookContextFields()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteStatus(temp.Status, "cli", "working", now, pid: 42,
            contextUsed: 7_000, contextMax: 250_000, promptTokens: 321);
        WriteRollout(temp.Sessions, "limits-only", "codex_cli_rs", now,
            EventAt(now, "token_count",
                "\"info\":null,\"rate_limits\":{\"primary\":{\"used_percent\":27,\"window_minutes\":300,\"resets_in_seconds\":60}}"));

        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => true, watchFiles: false,
            desktopPresence: StoppedDesktop);

        Assert.Equal(7_000, store.Current!.ContextUsed);
        Assert.Equal(250_000, store.Current.ContextMax);
        Assert.Equal(321, store.Current.PromptTokens);
        Assert.Equal(27, store.Current.PrimaryLimit!.UsedPercent);
    }

    [Fact]
    public void ForceRefresh_PartialTokenInfoMergesEachContextFieldIndependently()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteStatus(temp.Status, "cli", "working", now, pid: 42,
            contextUsed: 7_000, contextMax: 250_000, promptTokens: 321);
        WriteRollout(temp.Sessions, "partial-info", "codex_cli_rs", now,
            EventAt(now, "token_count", "\"info\":{\"total_token_usage\":{\"total_tokens\":8765}},\"rate_limits\":null"));

        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => true, watchFiles: false,
            desktopPresence: StoppedDesktop);

        Assert.Equal(8_765, store.Current!.ContextUsed);
        Assert.Equal(250_000, store.Current.ContextMax);
        Assert.Equal(321, store.Current.PromptTokens);
    }

    [Fact]
    public void Watcher_KeepsLastGoodDesktopDuringMalformedReplacementThenPublishesFinalFile()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteStatus(temp.Status, "desktop", "working", now, pid: 7, currentTool: "first");

        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => true, watchFiles: true,
            desktopPresence: RunningDesktop);
        var initialVersion = store.Version;
        File.WriteAllText(Path.Combine(temp.Status, "desktop.json"), "{partial");

        Assert.True(SpinWait.SpinUntil(() => store.Version > initialVersion, TimeSpan.FromSeconds(5)));
        Assert.Equal(CodexSurface.Desktop, store.Current!.Source);
        Assert.Equal("first", store.Current.CurrentTool);

        WriteStatus(temp.Status, "desktop", "working", now.AddSeconds(1), pid: 7, currentTool: "final");

        Assert.True(SpinWait.SpinUntil(() => store.Current?.CurrentTool == "final", TimeSpan.FromSeconds(5)));
        Assert.Equal(CodexSurface.Desktop, store.Current!.Source);
    }

    [Fact]
    public void StatusWatcher_DoesNotReparseUnrelatedRolloutHistory()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteStatus(temp.Status, "cli", "working", now, pid: 9, currentTool: "first");
        for (var index = 0; index < 8; index++)
        {
            WriteRollout(temp.Sessions, $"history-{index}", "codex_cli_rs", now.AddMinutes(index - 8),
                EventAt(now.AddMinutes(index - 8), "task_complete", "\"completed_at\":1"));
        }

        var parseCount = 0;
        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => true, watchFiles: true, parseRollout: path =>
        {
            Interlocked.Increment(ref parseCount);
            return CodexRollout.Parse(path);
        }, desktopPresence: StoppedDesktop);
        var initialVersion = store.Version;
        var initialParseCount = parseCount;

        WriteStatus(temp.Status, "cli", "working", now.AddSeconds(1), pid: 9, currentTool: "status-only");

        Assert.True(SpinWait.SpinUntil(() => store.Version > initialVersion, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, initialParseCount);
        Assert.Equal(initialParseCount, parseCount);
        Assert.Equal("status-only", store.Current!.CurrentTool);
    }

    [Fact]
    public void WatcherError_SchedulesFullRolloutRecovery()
    {
        using var temp = new TempDirectory();
        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => false, watchFiles: false,
            desktopPresence: RunningDesktop);
        var initialVersion = store.Version;
        var now = DateTimeOffset.UtcNow;
        WriteRollout(temp.Sessions, "recovered", "Codex Desktop", now,
            EventAt(now, "task_started", "\"model_context_window\":353400"));

        store.HandleWatcherError(null, new ErrorEventArgs(new InternalBufferOverflowException()));

        Assert.True(SpinWait.SpinUntil(() => store.Version > initialVersion, TimeSpan.FromSeconds(5)));
        Assert.Equal(CodexSurface.Desktop, store.Current!.Source);
        Assert.Equal("working", store.Current.State);
    }

    [Fact]
    public void Polling_NormalizesStaleDesktopRolloutWithoutFilesystemWork()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        WriteRollout(temp.Sessions, "expiring", "Codex Desktop", now,
            EventAt(now, "task_started", "\"model_context_window\":353400"));
        var parseCount = 0;
        using var store = new CodexStatusStore(temp.Status, temp.Sessions, _ => false, watchFiles: false,
            parseRollout: path =>
            {
                Interlocked.Increment(ref parseCount);
                return CodexRollout.Parse(path);
            },
            clock: () => now,
            desktopPresence: RunningDesktop);
        var initialVersion = store.Version;
        var initialParseCount = parseCount;

        Assert.Equal(CodexSurface.Desktop, store.Current!.Source);

        now = now.AddSeconds(31);

        Assert.Equal("idle", store.Current!.State);
        Assert.True(store.Version > initialVersion);
        Assert.Equal(initialParseCount, parseCount);
    }

    [Fact]
    public void Polling_FallsBackToCliWhenDesktopProcessExits()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow;
        var livePids = new HashSet<int> { 101, 202 };
        WriteStatus(temp.Status, "desktop", "working", now.AddMinutes(-5), pid: 101);
        WriteStatus(temp.Status, "cli", "working", now.AddMinutes(-5), pid: 202);
        using var store = new CodexStatusStore(temp.Status, temp.Sessions, livePids.Contains, watchFiles: false,
            clock: () => now,
            desktopPresence: () => livePids.Contains(101) ? RunningDesktop() : StoppedDesktop());
        var initialVersion = store.Version;

        Assert.Equal(CodexSurface.Desktop, store.Current!.Source);

        livePids.Remove(101);

        Assert.Equal(CodexSurface.Cli, store.Current!.Source);
        Assert.True(store.Version > initialVersion);
    }

    private static CodexSnapshot Snapshot(CodexSurface source, DateTimeOffset updatedAt, bool alive) => new(
        source, "working", null, null, null, null, null, 0, 0, 0, 0, 0, null, null, updatedAt, alive);

    private static CodexDesktopPresence RunningDesktop() => new(true, DateTimeOffset.MinValue);

    private static CodexDesktopPresence StoppedDesktop() => new(false, default);

    private static string TempRollout(params string[] events)
    {
        var path = Path.Combine(Path.GetTempPath(), $"halo-codex-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, events);
        return path;
    }

    private static string Event(string type, string payload) =>
        EventAt(DateTimeOffset.Parse("2026-07-16T12:00:00Z"), type, payload);

    private static string EventAt(DateTimeOffset timestamp, string type, string payload) =>
        $"{{\"timestamp\":\"{timestamp:O}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"{type}\",{payload}}}}}";

    private static string ToolCall(string name) => Event("custom_tool_call", $"\"name\":\"{name}\"");

    private static string TokenCount(long total, long context, double primaryUsed, int primaryWindow, long primaryReset,
        double? secondaryUsed = null, int secondaryWindow = 0, long secondaryResetInSeconds = 0)
    {
        var secondary = secondaryUsed is null ? string.Empty :
            $",\"secondary\":{{\"used_percent\":{secondaryUsed},\"window_minutes\":{secondaryWindow},\"resets_in_seconds\":{secondaryResetInSeconds}}}";
        return Event("token_count", $"\"info\":{{\"total_token_usage\":{{\"total_tokens\":{total}}},\"model_context_window\":{context}}},\"rate_limits\":{{\"primary\":{{\"used_percent\":{primaryUsed},\"window_minutes\":{primaryWindow},\"resets_at\":{primaryReset}}}{secondary}}}");
    }

    private static void WriteRollout(string sessions, string name, string originator, DateTimeOffset timestamp, params string[] events)
    {
        var directory = Path.Combine(sessions, timestamp.ToString("yyyy"), timestamp.ToString("MM"), timestamp.ToString("dd"));
        Directory.CreateDirectory(directory);
        var metadata = $"{{\"timestamp\":\"{timestamp:O}\",\"type\":\"session_meta\",\"payload\":{{\"originator\":\"{originator}\",\"cwd\":\"C:\\\\repo\"}}}}";
        var path = Path.Combine(directory, $"rollout-{name}.jsonl");
        File.WriteAllLines(path, [metadata, .. events]);
        File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
    }

    private static void WriteStatus(string directory, string surface, string state, DateTimeOffset updatedAt, int pid,
        string? currentTool = null, bool? processAlive = null, long contextUsed = 0, long contextMax = 0,
        long promptTokens = 0)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            source = surface,
            state,
            currentTool,
            pid,
            updatedAt,
            processAlive,
            contextUsed,
            contextMax,
            promptTokens,
        });
        File.WriteAllText(Path.Combine(directory, $"{surface}.json"), json);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"halo-codex-tests-{Guid.NewGuid():N}");
        internal string Status => Path.Combine(Root, "notch");
        internal string Sessions => Path.Combine(Root, "sessions");

        internal TempDirectory()
        {
            Directory.CreateDirectory(Status);
            Directory.CreateDirectory(Sessions);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
