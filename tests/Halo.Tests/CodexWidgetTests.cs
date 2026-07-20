using Halo.Widgets;

namespace Halo.Tests;

public sealed class CodexWidgetTests
{
    [Theory]
    [InlineData("working", "exec", false, false, "running…")]
    [InlineData("working", "apply_patch", false, false, "patching…")]
    [InlineData("working", "web_search", false, false, "googling :P")]
    [InlineData("waiting_input", null, false, false, "your move ;)")]
    [InlineData("idle", null, false, false, "let's work :)")]
    [InlineData("working", null, true, false, "api error :(")]
    public void MoodAndVerbMatchClaudeSemantics(string state, string? tool, bool apiDown, bool netDown, string expected)
        => Assert.Equal(expected, CodexWidget.DisplayText(state, tool, apiDown, netDown));
}
