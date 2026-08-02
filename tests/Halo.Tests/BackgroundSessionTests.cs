using Halo.ClaudeCode;

namespace Halo.Tests;

// The claude daemon respawns persisted background/fork sessions as headless processes that never end.
// The hooks tag them background:true (their claude process is parented by claude, not a shell); the
// store hides them unless they are actually doing something the user could care about or act on.
public class BackgroundSessionTests
{
    private static CcStatus St(bool background, string? state) =>
        new() { Background = background, State = state };

    [Fact]
    public void Idle_background_session_is_hidden()
        => Assert.True(StatusStore.BackgroundHidden(St(true, "idle")));

    [Fact]
    public void Stateless_background_session_is_hidden()
        => Assert.True(StatusStore.BackgroundHidden(St(true, null)));

    [Theory]
    [InlineData("working")]
    [InlineData("compacting")]
    [InlineData("waiting_input")] // the pill's ask flow is how a headless session gets answered
    public void Busy_background_session_stays_visible(string state)
        => Assert.False(StatusStore.BackgroundHidden(St(true, state)));

    [Theory]
    [InlineData("idle")]
    [InlineData("working")]
    [InlineData(null)]
    public void Foreground_sessions_are_never_hidden_by_this_gate(string? state)
        => Assert.False(StatusStore.BackgroundHidden(St(false, state)));
}
