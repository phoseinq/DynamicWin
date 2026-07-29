using Halo.Widgets;

namespace Halo.Tests;

public sealed class CodexWidgetTests
{
    [Fact]
    public void CollapsedRendering_ObservesLatestLimitsWithoutOpeningPanel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"halo-codex-widget-{Guid.NewGuid():N}");
        var status = Path.Combine(root, "notch");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(status);
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(status, "cli.json"),
            $"{{\"state\":\"working\",\"pid\":42,\"updatedAt\":\"{now:O}\",\"primaryLimit\":{{\"usedPercent\":67,\"windowMinutes\":300}}}}");

        try
        {
            using var store = new Halo.Codex.CodexStatusStore(status, sessions, _ => true, false, clock: () => now);
            Halo.Codex.CodexSnapshot? observed = null;
            var widget = new CodexWidget(store, Halo.Codex.CodexSurface.Cli, () => { }, observeLimits: value => observed = value);
            using var bitmap = new System.Drawing.Bitmap(220, 40);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);

            widget.DrawCollapsed(graphics, 220, 40, 1f);

            Assert.Equal(67, observed!.PrimaryLimit!.UsedPercent);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void GroupRowImage_UsesPlainCodexMarkAndOpticalLeftOffset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"halo-codex-icon-{Guid.NewGuid():N}");
        var status = Path.Combine(root, "notch");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(status);
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.UtcNow;
        var json = $"{{\"state\":\"working\",\"pid\":42,\"updatedAt\":\"{now:O}\"}}";
        File.WriteAllText(Path.Combine(status, "desktop.json"), json);
        File.WriteAllText(Path.Combine(status, "cli.json"), json);

        try
        {
            using var store = new Halo.Codex.CodexStatusStore(status, sessions, _ => true, false,
                clock: () => now,
                desktopPresence: () => new Halo.Codex.CodexDesktopPresence(true, now.AddMinutes(-1)));
            var desktop = new CodexWidget(store, Halo.Codex.CodexSurface.Desktop, () => { });
            IWidget[] widgets = [desktop, new CodexWidget(store, Halo.Codex.CodexSurface.Cli, () => { })];

            var image = Halo.Shell.NotchController.MenuRowImage(widgets, [0, 1]);

            Assert.NotNull(CodexWidget.PlainIcon);
            Assert.Same(CodexWidget.PlainIcon, image);
            Assert.NotSame(desktop.IconImage, image);
            Assert.InRange(Halo.Shell.NotchController.MenuRowImageOffset(widgets, [0, 1]), -2f, -0.5f);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // What is being pinned is the MAPPING - this state and this tool mean that situation - not the
    // exact words, which now come from a set per slot and vary by design. Asserting a literal here
    // would break every time the wording is widened, and would be testing the copy rather than the
    // routing that actually has logic in it.
    [Theory]
    [InlineData("working", "exec", false, false, "running")]
    [InlineData("working", "shell_command", false, false, "running")]
    [InlineData("working", "apply_patch", false, false, "patching")]
    [InlineData("working", "web_search", false, false, "searching")]
    [InlineData("idle", null, false, false, "idle")]
    [InlineData("working", null, true, false, "apiError")]
    public void MoodAndVerbMatchClaudeSemantics(string state, string? tool, bool apiDown, bool netDown, string slot)
        => Assert.Contains(CodexWidget.DisplayText(state, tool, apiDown, netDown), Halo.Agents.Moods.Set(slot));

    // not a mood slot: the question is addressed to you, so it is one fixed sentence on purpose
    [Fact]
    public void WaitingForYouIsNotARotatingLine()
        => Assert.Equal("your move ;)", CodexWidget.DisplayText("waiting_input", null, false, false));
}
