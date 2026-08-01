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
                case "general.reset":
                    var offset = Path.Combine(HaloDir, "offset");
                    if (File.Exists(offset)) File.Delete(offset);
                    break;
                case "access.notifications":
                    Open("ms-settings:privacy-notifications");
                    break;
                case "access.startup":
                    Open(Environment.GetFolderPath(Environment.SpecialFolder.Startup));
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
