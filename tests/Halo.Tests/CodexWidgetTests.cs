using Halo.Widgets;

namespace Halo.Tests;

public sealed class CodexWidgetTests
{
    [Theory]
    [InlineData("working", "Edit", false, false, "writing…")]
    [InlineData("waiting_input", null, false, false, "your move ;)")]
    [InlineData("idle", null, false, false, "let's work :)")]
    [InlineData("working", null, true, false, "api error :(")]
    public void MoodAndVerbMatchClaudeSemantics(string state, string? tool, bool apiDown, bool netDown, string expected)
        => Assert.Equal(expected, CodexWidget.DisplayText(state, tool, apiDown, netDown));
}
