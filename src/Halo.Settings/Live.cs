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
        "access.startup" => StartupShortcut ? "On" : "Missing",
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

    // The installer writes this; the toggle on General is meant to add and remove it. Reading the file
    // rather than the setting is the point of a Status row - it reports what Windows will actually do.
    private static bool StartupShortcut
    {
        get
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                foreach (var link in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    string name = Path.GetFileNameWithoutExtension(link);
                    if (name.Contains("Halo", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("DynamicWin", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
