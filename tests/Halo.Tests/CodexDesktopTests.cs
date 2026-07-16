using Halo.Codex;

namespace Halo.Tests;

public sealed class CodexDesktopTests
{
    [Fact]
    public void DesktopCancel_PostsOneEscapePair()
    {
        var posted = new List<uint>();
        var window = new CodexDesktopWindow(
            "ChatGPT",
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__test\app\ChatGPT.exe",
            new IntPtr(42),
            DateTimeOffset.UtcNow.AddHours(-1));
        var runtime = new CodexDesktopRuntime(
            () => [window],
            (_, message, _, _) => { posted.Add(message); return true; },
            () => DateTimeOffset.UtcNow);

        Assert.True(runtime.TryCancel());
        Assert.Equal(new uint[] { 0x0100, 0x0101 }, posted);
    }
}
