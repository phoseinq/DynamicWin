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
