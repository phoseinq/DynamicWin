using System;
using Halo.ClaudeCode;
using Halo.Widgets;
using Xunit;

namespace Halo.Tests;

// Reported live: cancel a turn - with Esc or with the panel's stop button, which injects Esc - and the
// pill stayed on "hmm…" indefinitely. Nothing writes a status for an interrupt, and a pid-backed status
// counts as live for as long as the process runs, so the last "working" never expired.
public class TurnOverTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-29T12:00:00Z");

    private static CcStatus Working(string tool = "", int agoSeconds = 1, string started = "2026-07-29T11:59:00Z")
        => new()
        {
            State = "working",
            CurrentTool = tool,
            StartedAt = started,
            UpdatedAt = Now.AddSeconds(-agoSeconds).ToString("o"),
            Pid = 4242,
        };

    [Fact]
    public void AFreshTurnIsNotOver()
        => Assert.False(ClaudeCodeWidget.TurnOver(Working(), Now));

    [Fact]
    public void ARunningToolIsNeverAgedOut()
    {
        // a long Bash writes nothing for as long as it runs, so time alone must not end it - the tool
        // name on the status is what says it is still going
        var st = Working(tool: "Bash", agoSeconds: ClaudeCodeWidget.SettleAfterSeconds * 10);
        Assert.False(ClaudeCodeWidget.TurnOver(st, Now));
    }

    [Fact]
    public void AToolLessTurnAgesOutOnlyAfterTheGrace()
    {
        Assert.False(ClaudeCodeWidget.TurnOver(
            Working(agoSeconds: ClaudeCodeWidget.SettleAfterSeconds - 1), Now));
        Assert.True(ClaudeCodeWidget.TurnOver(
            Working(agoSeconds: ClaudeCodeWidget.SettleAfterSeconds + 1), Now));
    }

    [Fact]
    public void TheLatchEndsTheTurnAtOnceAndOnlyThatTurn()
    {
        var st = Working(started: "2026-07-29T11:59:00Z");
        ClaudeCodeWidget.MarkTurnCancelled(st.StartedAt);
        Assert.True(ClaudeCodeWidget.TurnOver(st, Now));

        // the next turn carries its own stamp, so the latch stops matching by itself - there is nothing
        // to clear, which is what keeps a cancel from silencing every turn after it
        var next = Working(started: "2026-07-29T12:00:30Z");
        Assert.False(ClaudeCodeWidget.TurnOver(next, Now));
        ClaudeCodeWidget.MarkTurnCancelled(null);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("waiting_input")]
    [InlineData("compacting")]
    public void OnlyAWorkingTurnIsSubjectToThis(string state)
    {
        var st = Working(agoSeconds: ClaudeCodeWidget.SettleAfterSeconds * 10);
        st.State = state;
        Assert.False(ClaudeCodeWidget.TurnOver(st, Now));
    }

    [Fact]
    public void NoSessionIsNotAnEndedTurn()
        => Assert.False(ClaudeCodeWidget.TurnOver(null, Now));

    // ---- and the twin, which had the identical hole for the identical reason. Same cases, because the
    // bug is not vendor-specific: an interrupt is not a lifecycle event on either side.

    private static Halo.Codex.CodexSnapshot CodexWorking(
        string? tool = null, int agoSeconds = 1, string started = "2026-07-29T11:59:00Z")
        => new(Halo.Codex.CodexSurface.Cli, "working", tool, DateTimeOffset.Parse(started),
            null, null, null, 4242, 4242, 0, 0, 0, null, null, Now.AddSeconds(-agoSeconds), true);

    [Fact]
    public void CodexFreshTurnIsNotOver()
        => Assert.False(CodexWidget.TurnOver(CodexWorking(), Now));

    [Fact]
    public void CodexRunningToolIsNeverAgedOut()
        => Assert.False(CodexWidget.TurnOver(
            CodexWorking(tool: "exec", agoSeconds: CodexWidget.SettleAfterSeconds * 10), Now));

    [Fact]
    public void CodexToolLessTurnAgesOutOnlyAfterTheGrace()
    {
        Assert.False(CodexWidget.TurnOver(
            CodexWorking(agoSeconds: CodexWidget.SettleAfterSeconds - 1), Now));
        Assert.True(CodexWidget.TurnOver(
            CodexWorking(agoSeconds: CodexWidget.SettleAfterSeconds + 1), Now));
    }

    [Fact]
    public void CodexLatchEndsTheTurnAtOnceAndOnlyThatTurn()
    {
        var st = CodexWorking();
        CodexWidget.MarkTurnCancelled(st.StartedAt);
        Assert.True(CodexWidget.TurnOver(st, Now));

        // …and the stop button goes with it: a turn the pill has stopped believing in must not send a
        // second Esc into whatever owns that terminal now
        Assert.Equal(CodexCancelRoute.None, CodexWidget.GetCancelRoute(st, canCancelDesktop: true));

        var next = CodexWorking(started: "2026-07-29T12:00:30Z");
        Assert.False(CodexWidget.TurnOver(next, Now));
        CodexWidget.MarkTurnCancelled(null);
    }

    [Fact]
    public void CodexTurnWithNoStampAtAllIsNotAgedOut()
    {
        // UpdatedAt is non-nullable on the Codex snapshot, so "never written" arrives as default - which is
        // an absent measurement, not a very old one
        var st = new Halo.Codex.CodexSnapshot(Halo.Codex.CodexSurface.Cli, "working", null, null,
            null, null, null, 1, 1, 0, 0, 0, null, null, default, true);
        Assert.False(CodexWidget.TurnOver(st, Now));
    }
}
