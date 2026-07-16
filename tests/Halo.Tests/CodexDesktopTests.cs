using Halo.Codex;
using Halo.Widgets;

namespace Halo.Tests;

public sealed class CodexDesktopTests
{
    [Fact]
    public void DesktopPresence_CachesProbeFor500Milliseconds()
    {
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var scans = 0;
        var runtime = new CodexDesktopRuntime(
            () =>
            {
                scans++;
                return [Window(now.AddHours(-1))];
            },
            (_, _, _, _) => true,
            () => now);

        Assert.True(runtime.Presence.Running);
        now = now.AddMilliseconds(499);
        Assert.True(runtime.Presence.Running);
        Assert.Equal(1, scans);

        now = now.AddMilliseconds(1);
        Assert.True(runtime.Presence.Running);
        Assert.Equal(2, scans);
    }

    [Fact]
    public void DesktopCancel_PostsOneEscapePair()
    {
        var posted = new List<uint>();
        var window = Window(DateTimeOffset.UtcNow.AddHours(-1));
        var runtime = new CodexDesktopRuntime(
            () => [window],
            (_, message, _, _) => { posted.Add(message); return true; },
            () => DateTimeOffset.UtcNow);

        Assert.True(runtime.TryCancel());
        Assert.Equal(new uint[] { 0x0100, 0x0101 }, posted);
    }

    [Fact]
    public void DesktopCancel_ThrottlesForOneSecond()
    {
        var now = DateTimeOffset.Parse("2026-07-16T12:00:00Z");
        var posted = new List<uint>();
        var runtime = new CodexDesktopRuntime(
            () => [Window(now.AddHours(-1))],
            (_, message, _, _) => { posted.Add(message); return true; },
            () => now);

        Assert.True(runtime.TryCancel());
        Assert.False(runtime.TryCancel());
        now = now.AddMilliseconds(999);
        Assert.False(runtime.TryCancel());
        Assert.Equal(2, posted.Count);

        now = now.AddMilliseconds(1);
        Assert.True(runtime.TryCancel());
        Assert.Equal(4, posted.Count);
    }

    [Fact]
    public void CodexWidgetCancel_CliRequiresConsolePid()
    {
        Assert.Equal(CodexCancelRoute.None,
            CodexWidget.GetCancelRoute(Snapshot(CodexSurface.Cli, consolePid: 0), canCancelDesktop: true));
        Assert.Equal(CodexCancelRoute.Cli,
            CodexWidget.GetCancelRoute(Snapshot(CodexSurface.Cli, consolePid: 42), canCancelDesktop: false));
    }

    [Fact]
    public void CodexWidgetCancel_DesktopRequiresDesktopCapability()
    {
        Assert.Equal(CodexCancelRoute.None,
            CodexWidget.GetCancelRoute(Snapshot(CodexSurface.Desktop), canCancelDesktop: false));
        Assert.Equal(CodexCancelRoute.Desktop,
            CodexWidget.GetCancelRoute(Snapshot(CodexSurface.Desktop), canCancelDesktop: true));
    }

    private static CodexDesktopWindow Window(DateTimeOffset startedAt) => new(
        "ChatGPT",
        @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__test\app\ChatGPT.exe",
        new IntPtr(42),
        startedAt);

    private static CodexSnapshot Snapshot(CodexSurface source, int consolePid = 0) => new(
        source, "working", null, null, null, null, null, 0, consolePid, 0, 0, 0,
        null, null, DateTimeOffset.UtcNow, true);
}
