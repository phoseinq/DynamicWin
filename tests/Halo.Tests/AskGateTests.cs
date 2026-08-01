using System.Text.Json.Nodes;
using Halo.Hooks;

namespace Halo.Tests;

// PreToolUse fires for EVERY tool call, not only the ones Claude would have prompted about - it decides
// that after hooks run. So the gate has to answer "would this have prompted?" itself, and the expensive
// mistake is in both directions: say yes too often and Halo raises a banner for the `git status` already
// on your allowlist, say yes too rarely and the feature does nothing.
//
// Silence is always the safe answer: no ask means the terminal prompt stands exactly as it does today.
// That is why every malformed input below expects false.
public sealed class AskGateTests
{
    private static JsonObject Input(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static readonly string[] NoRules = [];

    // ---- AskUserQuestion ----

    private const string OneQuestion =
        """{"questions":[{"question":"Which?","header":"H","options":[{"label":"A","description":"a"},{"label":"B","description":"b"}]}]}""";

    private const string TwoQuestions =
        """{"questions":[{"question":"One?","header":"A","options":[{"label":"x","description":"x"}]},{"question":"Two?","header":"B","options":[{"label":"y","description":"y"}]}]}""";

    [Fact]
    public void SingleQuestionIsAskable()
        => Assert.True(AskGate.ShouldAsk("AskUserQuestion", Input(OneQuestion), NoRules));

    // a 220px pill is not a four-question form, and half an answer is worse than none
    [Fact]
    public void MultiQuestionIsNotIntercepted()
        => Assert.False(AskGate.ShouldAsk("AskUserQuestion", Input(TwoQuestions), NoRules));

    [Theory]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"questions":"not an array"}""")]
    public void MalformedQuestionFallsSilent(string json)
        => Assert.False(AskGate.ShouldAsk("AskUserQuestion", Input(json), NoRules));

    // an allow rule never suppresses a question: it is not a permission, it is a choice being asked for
    [Fact]
    public void AllowRulesDoNotSuppressAQuestion()
        => Assert.True(AskGate.ShouldAsk("AskUserQuestion", Input(OneQuestion), ["AskUserQuestion"]));

    // ---- ordinary tools ----

    // v1 does not intercept permissions at all. "Not in permissions.allow" turned out to be a far wider
    // net than "Claude would have prompted" - session approvals and permission modes are invisible to a
    // hook - and on a real allow list that meant a 20s block on nearly every Read and Edit.
    [Fact]
    public void OrdinaryToolsAreNotInterceptedInV1()
        => Assert.False(AskGate.ShouldAsk("Bash", Input("""{"command":"rm -rf build"}"""), ["Bash(git status:*)"]));

    [Fact]
    public void ToolCoveredByAnAllowRuleStaysSilent()
        => Assert.False(AskGate.ShouldAsk("Bash", Input("""{"command":"git status --short"}"""),
            ["Bash(git status:*)"]));

    // the opt-in path the matcher exists for: with permissions switched on, the rules decide again
    [Fact]
    public void WithPermissionsOnTheRulesDecideAgain()
    {
        AskGate.AnswerPermissions = true;
        try
        {
            Assert.True(AskGate.ShouldAsk("Bash", Input("""{"command":"rm -rf build"}"""), ["Bash(git status:*)"]));
            Assert.False(AskGate.ShouldAsk("Bash", Input("""{"command":"git status --short"}"""),
                ["Bash(git status:*)"]));
        }
        finally { AskGate.AnswerPermissions = false; }
    }

    [Fact]
    public void MissingToolNameFallsSilent()
        => Assert.False(AskGate.ShouldAsk(null, Input("""{"command":"anything"}"""), NoRules));

    // ---- the allow-rule matcher, as its own table ----

    // a bare tool name is a blanket allow for that tool, whatever the argument
    [Theory]
    [InlineData("Read", "Read", "C:/any/file.cs", true)]
    [InlineData("Read", "Bash", "git status", false)]
    public void BareRuleMatchesTheWholeTool(string rule, string tool, string target, bool expected)
        => Assert.Equal(expected, AskGate.AllowRuleMatches(rule, tool, target));

    // "prefix:*" is Claude Code's own form and means "the command starts with prefix" - the colon is a
    // separator, not a character the command has to contain. Globbing it naively matches nothing.
    [Theory]
    [InlineData("Bash(git status:*)", "git status", true)]
    [InlineData("Bash(git status:*)", "git status --short", true)]
    [InlineData("Bash(git status:*)", "git stat", false)]
    [InlineData("Bash(git status:*)", "sudo git status", false)]
    public void PrefixRuleMatchesFromTheStart(string rule, string command, bool expected)
        => Assert.Equal(expected, AskGate.AllowRuleMatches(rule, "Bash", command));

    [Theory]
    [InlineData("Edit(src/*.cs)", "src/a.cs", true)]
    [InlineData("Edit(src/*.cs)", "src/deep/a.cs", true)]
    [InlineData("Edit(src/*.cs)", "other/a.cs", false)]
    [InlineData("Edit(exact.cs)", "exact.cs", true)]
    [InlineData("Edit(exact.cs)", "exact.csx", false)]
    public void PatternRuleMatchesTheTarget(string rule, string path, bool expected)
        => Assert.Equal(expected, AskGate.AllowRuleMatches(rule, "Edit", path));

    // a rule with a pattern cannot match a call whose target we could not read; a bare rule still can
    [Fact]
    public void PatternRuleCannotMatchAnUnknownTarget()
        => Assert.False(AskGate.AllowRuleMatches("Bash(git status:*)", "Bash", null));

    [Theory]
    [InlineData("")]
    [InlineData("Bash(unclosed")]
    [InlineData("()")]
    public void MalformedRuleNeverMatches(string rule)
        => Assert.False(AskGate.AllowRuleMatches(rule, "Bash", "git status"));

    // ---- target extraction ----

    [Theory]
    [InlineData("Bash", """{"command":"git status"}""", "git status")]
    [InlineData("Read", """{"file_path":"C:/x.cs"}""", "C:/x.cs")]
    [InlineData("Edit", """{"file_path":"C:/x.cs"}""", "C:/x.cs")]
    [InlineData("WebFetch", """{"url":"https://example.com"}""", "https://example.com")]
    [InlineData("Bash", """{}""", null)]
    [InlineData("Unknown", """{"command":"x"}""", null)]
    public void TargetComesFromTheToolsOwnField(string tool, string json, string? expected)
        => Assert.Equal(expected, AskGate.TargetOf(tool, Input(json)));
}
