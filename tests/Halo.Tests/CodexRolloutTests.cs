using Halo.Codex;

namespace Halo.Tests;

public sealed class CodexRolloutTests
{
    [Fact]
    public void Parse_UsesLatestTokenCountAndTaskState()
    {
        var path = TempRollout(
            Event("task_started", "\"model_context_window\":353400"),
            TokenCount(total: 18420, context: 353400, primaryUsed: 37, primaryWindow: 300, primaryReset: 1784808749),
            ToolCall("functions.exec"));

        var value = CodexRollout.Parse(path)!;

        Assert.Equal("working", value.State);
        Assert.Equal("exec", value.CurrentTool);
        Assert.Equal(18_420, value.ContextUsed);
        Assert.Equal(353_400, value.ContextMax);
        Assert.Equal(37, value.PrimaryLimit!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784808749), value.PrimaryLimit.ResetsAt);
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

    private static CodexSnapshot Snapshot(CodexSurface source, DateTimeOffset updatedAt, bool alive) => new(
        source, "working", null, null, null, null, null, 0, 0, 0, 0, 0, null, null, updatedAt, alive);

    private static string TempRollout(params string[] events)
    {
        var path = Path.Combine(Path.GetTempPath(), $"halo-codex-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, events);
        return path;
    }

    private static string Event(string type, string payload) =>
        $"{{\"timestamp\":\"2026-07-16T12:00:00Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"{type}\",{payload}}}}}";

    private static string ToolCall(string name) => Event("custom_tool_call", $"\"name\":\"{name}\"");

    private static string TokenCount(long total, long context, double primaryUsed, int primaryWindow, long primaryReset) =>
        Event("token_count", $"\"info\":{{\"total_token_usage\":{{\"total_tokens\":{total}}},\"model_context_window\":{context}}},\"rate_limits\":{{\"primary\":{{\"used_percent\":{primaryUsed},\"window_minutes\":{primaryWindow},\"resets_at\":{primaryReset}}}}}");
}
