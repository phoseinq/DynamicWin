using System;
using System.Globalization;
using System.IO;

namespace Halo.Shell;

internal enum GreetingKind
{
    None,
    Install,   // never run on this machine: open up and introduce yourself
    Login,     // first run since Windows came up: just the hand, in the collapsed pill
}

// Whether this launch gets a greeting, and which one.
//
// The hard requirement is the one that is easy to get wrong: Halo restarts itself whenever the settings
// panel applies a change, and a greeting on every one of those would be intolerable. So "has Halo ever
// run" is not the question - the question is "has Halo run since this Windows session began", and the
// answer is a boot timestamp written beside the other loose state under %LOCALAPPDATA%\Halo.
//
// Boot time is derived from the tick count rather than asked of WMI, which this project does not use. The
// known wrinkle is that the tick count does not advance while the machine is suspended, so a long sleep
// walks the derived boot time forward and can spend one extra greeting on resume. That is the right side
// to be wrong on: coming back from sleep does feel like the machine starting, and the case that must
// never misfire - two launches seconds apart across a settings restart - is nowhere near the tolerance.
internal static class GreetingGate
{
    // Generous next to the sub-second drift of a settings restart, and nowhere near a real reboot.
    internal static readonly TimeSpan Slack = TimeSpan.FromMinutes(2);

    internal static GreetingKind Decide(string? stamp, DateTime bootUtc)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return GreetingKind.Install;
        if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var greeted))
            return GreetingKind.Install;   // unreadable is the same as never, and re-greeting beats never
        // A stamp from the FUTURE is a clock that moved, not a boot that has not happened - treat it as
        // this session, or a timezone change would greet on every launch until the clock caught up.
        var gap = bootUtc - greeted.ToUniversalTime();
        return gap > Slack || gap < -Slack ? GreetingKind.Login : GreetingKind.None;
    }

    internal static DateTime BootUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    internal static string Stamp(DateTime bootUtc) => bootUtc.ToString("o", CultureInfo.InvariantCulture);

    // Reading and writing it is best-effort in both directions, like every other loose file here: a state
    // directory that cannot be written must cost a greeting, not a launch.
    internal static GreetingKind Read(string path)
    {
        try { return Decide(File.Exists(path) ? File.ReadAllText(path) : null, BootUtc()); }
        catch { return GreetingKind.None; }
    }

    internal static void Mark(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Stamp(BootUtc()));
        }
        catch { }
    }
}
