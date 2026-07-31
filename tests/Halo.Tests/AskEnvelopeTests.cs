using Halo.Hooks;

namespace Halo.Tests;

public sealed class AskEnvelopeTests
{
    private static AskEnvelope Sample(DateTimeOffset expires) => new(
        "n1", 4242, "sess-a", "AskUserQuestion", null, "Which one?",
        [new AskOption("Keep", "leave it"), new AskOption("Drop", "remove it")], expires);

    [Fact]
    public void RoundTripsThroughJson()
    {
        var expires = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var back = AskEnvelope.FromJson(Sample(expires).ToJson());

        Assert.NotNull(back);
        Assert.Equal("n1", back!.Nonce);
        Assert.Equal(4242, back.Pid);
        Assert.Equal("sess-a", back.Session);
        Assert.Equal("AskUserQuestion", back.Tool);
        Assert.Equal("Which one?", back.Question);
        Assert.Equal(expires, back.ExpiresAt);
        Assert.Collection(back.Options,
            o => Assert.Equal("Keep", o.Label),
            o => Assert.Equal("Drop", o.Label));
    }

    // wall-clock UTC on purpose: a machine that slept must come back to an EXPIRED question, not to one
    // whose deadline slid forward with it
    [Fact]
    public void ExpiryIsAWallClockDeadline()
    {
        var expires = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var e = Sample(expires);
        Assert.False(e.IsExpired(expires.AddSeconds(-1)));
        Assert.True(e.IsExpired(expires));
        Assert.True(e.IsExpired(expires.AddHours(1)));
    }

    // hooks are deployed separately from the pill here, so a newer writer must not break an older reader
    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        string json = """{"nonce":"n2","pid":7,"tool":"Bash","expiresAt":"2026-08-01T12:00:00+00:00","somethingNew":{"a":1}}""";
        var back = AskEnvelope.FromJson(json);
        Assert.NotNull(back);
        Assert.Equal("n2", back!.Nonce);
        Assert.Empty(back.Options);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"pid":1}""")]
    [InlineData("""{"nonce":"n","tool":"Bash"}""")]
    [InlineData("""{"nonce":"n","tool":"Bash","expiresAt":"never"}""")]
    public void MalformedEnvelopeIsRejected(string? json) => Assert.Null(AskEnvelope.FromJson(json));

    // ---- the answer, and the one string this hook is allowed to print ----

    [Fact]
    public void AnswerRoundTrips()
    {
        var back = AskAnswer.FromJson(new AskAnswer("n1", "deny", "chose Keep").ToJson());
        Assert.NotNull(back);
        Assert.Equal("deny", back!.Decision);
        Assert.Equal("chose Keep", back.Reason);
    }

    [Theory]
    [InlineData("""{"nonce":"n","decision":"maybe"}""")]
    [InlineData("""{"decision":"allow"}""")]
    [InlineData("garbage")]
    public void MalformedAnswerIsRejected(string json) => Assert.Null(AskAnswer.FromJson(json));

    // Exact string: this is the interface Claude Code parses, not an internal detail. If it changes, that
    // is a decision about an external contract and the test should be the thing that says so.
    [Fact]
    public void HookStdoutIsTheAgreedShape()
        => Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","permissionDecisionReason":"approved from the pill"}}""",
            new AskAnswer("n1", "allow", "approved from the pill").ToHookStdout());

    // a pick is delivered as a deny naming the choice - a hook cannot return WHICH option was chosen, and
    // that trade was taken deliberately when the design was approved
    [Fact]
    public void AQuestionPickIsADenyNamingTheChoice()
        => Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Keep"}}""",
            new AskAnswer("n1", "deny", "Keep").ToHookStdout());
}
