using System;
using System.IO;

namespace Halo.Settings;

// What a Status row actually says. Every value here is read from the machine or is a statement of fact -
// never a guess dressed as a reading, which is the same rule the pill keeps about invented numbers. A
// permission nobody can probe from here says who owns it rather than claiming to know its state.
internal static class Live
{
    internal enum State { Neutral, Enabled, Attention }

    internal static string Value(Row row) => row.Key switch
    {
        // shown truncated: a token is meant to be copied, not read aloud, and a full one in a settings
        // window is a full one in every screen recording of a settings window
        "api.token" => Token,
        "about.version" => Version,
        "appearance.fpsMeasured" => Rate,
        "access.startup" => StartupTask ? "On" : "Missing",
        "access.notifications" => "Managed by Windows",
        _ => row.Fallback,
    };

    internal static State Tone(string value) => value.ToLowerInvariant() switch
    {
        "on" or "allowed" or "watching" => State.Enabled,
        "off" or "missing" or "denied" or "needs access" => State.Attention,
        _ => State.Neutral,
    };

    private static string Token
    {
        get
        {
            try
            {
                var store = new Store();
                string token = store.Text("api.token", "");
                return token.Length >= 8 ? token[..4] + "..." + token[^4..] : "Not generated yet";
            }
            catch { return "Not generated yet"; }
        }
    }

    private static string Version
    {
        get
        {
            try { return typeof(Live).Assembly.GetName().Version?.ToString(3) ?? "unknown"; }
            catch { return "unknown"; }
        }
    }

    // The panel cannot time the pill's frames - it is a different process - so the pill writes down what
    // it measured across its last morph and this reads the file back. Two integers and no sentence, so
    // the wording stays here. A missing or half-written file says so rather than filling in a number,
    // which is the same rule the pill keeps about invented percentages.
    private static string Rate
    {
        get
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "fps");
                var parts = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int measured = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
                int hz = parts.Length > 1 && int.TryParse(parts[1], out var h) ? h : 0;
                return Describe(measured, hz);
            }
            catch { return NotMeasured; }
        }
    }

    internal const string NotMeasured = "Not measured yet";

    internal static string Describe(int measured, int hz)
    {
        if (measured > 0 && hz > 0) return $"{measured} fps on a {hz} Hz display";
        if (measured > 0) return $"{measured} fps";
        if (hz > 0) return $"{hz} Hz display";
        return NotMeasured;
    }

    // Reading the machine rather than the setting is the point of a Status row - it reports what Windows
    // will actually do. It used to scan the Startup folder for a .lnk, which stopped being the truth the
    // day autostart became a logon-triggered scheduled task (Explorer released those shortcuts one at a
    // time and Halo came up last on every boot). So this row read "Missing" on a machine that was starting
    // Halo perfectly well. Halo.Hooks answers through its exit code, which is the same thing the installer
    // and the pill ask.
    private static bool StartupTask
    {
        get
        {
            try
            {
                string hooks = Path.Combine(AppContext.BaseDirectory, "Halo.Hooks.exe");
                if (!File.Exists(hooks)) return false;
                var psi = new System.Diagnostics.ProcessStartInfo(hooks)
                { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("query-autostart");
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                // bounded: a Status row must never be what hangs the window opening
                if (!p.WaitForExit(4000)) return false;
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
