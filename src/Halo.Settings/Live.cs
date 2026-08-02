using System;
using System.IO;

namespace Halo.Settings;

internal static class Live
{
    internal enum State { Neutral, Enabled, Attention }

    internal static string Value(Row row) => row.Key switch
    {

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
