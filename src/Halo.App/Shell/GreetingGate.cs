using System;
using System.IO;

namespace Halo.Shell;

internal enum GreetingKind
{
    None,
    Install,   // a build that has not run on this machine: open up and introduce yourself
    Login,     // any other start, and waking from sleep: just the hand, in the collapsed pill
}

// Whether this launch gets a greeting, and which one.
//
// It used to ask "has Halo run since Windows came up", derived from the tick count, so that the restart
// the settings panel performs on apply would stay silent. That answered the wrong question twice. A real
// reboot starts the INSTALLED copy from the Startup folder, so the greeting only ever appears there once
// a release carries it - and a laptop that sleeps rather than shuts down boots so rarely that the hand
// was, in practice, never seen at all.
//
// So the question now is simply "is this Halo starting": every launch gets the short hand, waking from
// sleep counts as one (the controller raises that, since there is no launch to observe), and the long
// introduction is kept for the one thing that really is new - a version that has not run here before.
// The marker under %LOCALAPPDATA%\Halo holds that version, and a settings restart writes the same string
// it read, which is what keeps the introduction from playing on every applied setting.
// When the hand is allowed to START. The clock used to begin with the process, and the logon task wins
// the race it was built to win: at a fresh boot Halo is up before explorer has a taskbar, so the 2.6s
// login hand played to a desktop nobody could see yet - "hello either never appears or shows up long
// after login". Waking has the twin problem: the first frame after a resume runs under the lock screen.
//
// So the greeting arms instead of playing: it waits until the screen is actually watchable (the shell
// has a taskbar AND the input desktop is openable, i.e. not the lock/secure desktop), then holds a
// short settle so it is not the first thing moving on a desktop still assembling itself. If the screen
// never becomes watchable - custom shell, kiosk - it gives up waiting and plays anyway, because a
// greeting that can be blocked forever would also block the strip and the pin, which yield to it.
internal static class GreetingArm
{
    internal const float SettleSeconds = 1.2f;
    internal const float GiveUpSeconds = 45f;

    // one step of the wait; pure so the arming is a test, not a boot-and-squint
    internal static (float held, float waited, bool armed) Step(bool watchable, float held, float waited, float dt)
    {
        waited += dt;
        held = watchable ? held + dt : 0f;   // a shell that vanishes mid-hold (explorer restart) starts over
        return (held, waited, held >= SettleSeconds || waited >= GiveUpSeconds);
    }
}

internal static class GreetingGate
{
    internal static GreetingKind Decide(string? marker, string version)
        => string.IsNullOrWhiteSpace(marker) || marker.Trim() != version
            ? GreetingKind.Install
            : GreetingKind.Login;

    // The installer's version, not a build stamp: rebuilding the same release during a working session
    // must not keep replaying the ten-second introduction, and a shipped upgrade must.
    internal static string Version =>
        typeof(GreetingGate).Assembly.GetName().Version?.ToString() ?? "0";

    // Reading and writing it is best-effort in both directions, like every other loose file here: a state
    // directory that cannot be written must cost a greeting, not a launch.
    internal static GreetingKind Read(string path)
    {
        try { return Decide(File.Exists(path) ? File.ReadAllText(path) : null, Version); }
        catch { return GreetingKind.Login; }
    }

    internal static void Mark(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Version);
        }
        catch { }
    }
}
