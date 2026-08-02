using System;
using System.Diagnostics;
using System.IO;

namespace Halo.Settings;

// The rows that DO something rather than store something. Each one goes through the same loose files
// the pill already keeps under %LOCALAPPDATA%\Halo, so the pill picks the change up on its own watcher
// and nothing has to be restarted.
internal static class Actions
{
    private static string HaloDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");

    internal static void Run(string key)
    {
        try
        {
            switch (key)
            {
                // the pill reads this file at startup and after every drag; deleting it is "centred"
                // Written, not deleted: the pill watches this file's timestamp and re-reads it, and a
                // deleted file has no timestamp to notice. Zero is centred.
                case "general.reset":
                    Directory.CreateDirectory(HaloDir);
                    File.WriteAllText(Path.Combine(HaloDir, "offset"), "0");
                    break;
                case "access.notifications":
                    Open("ms-settings:privacy-notifications");
                    break;
                // autostart is a logon-triggered scheduled task now, not a Startup-folder shortcut, so
                // opening that folder showed the user an empty window and no explanation
                case "access.startup":
                    Open("taskschd.msc");
                    break;
                // both of these rendered a button that did nothing at all, because Run had no case for them
                case "about.state":
                    Directory.CreateDirectory(HaloDir);
                    Open(HaloDir);
                    break;
                // the full token, not the truncated one the row displays
                case "api.token":
                    var token = new Store().Text("api.token", "");
                    if (token.Length > 0) System.Windows.Clipboard.SetText(token);
                    break;
                case "about.repo":
                    Open("https://github.com/phoseinq/DynamicWin");
                    break;
            }
        }
        catch { }
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
