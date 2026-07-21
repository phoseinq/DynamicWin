using System;
using System.Threading;
using Microsoft.Win32;

namespace Halo.Widgets;

// Apple-style privacy indicator: is any app using the mic (orange) or camera (green) right now?
// Windows records this under CapabilityAccessManager\ConsentStore — each app leaf carries a
// LastUsedTimeStop, and a value of 0 means "still in use". We poll that (cheap, ~1.2s) instead of
// hooking any device API. ponytail: HKCU only — covers user apps; add HKLM if a service ever needs it.
internal static class Privacy
{
    public static volatile bool Mic, Cam;
    public static int Version;                 // bumped on any change
    public static bool Active => Mic || Cam;

    private const string Base =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private static Timer? _timer;

    public static void Poke() => _timer ??= new Timer(_ => Scan(), null, 800, 1200);

    private static void Scan()
    {
        try
        {
            bool mic = InUse("microphone"), cam = InUse("webcam");
            if (mic == Mic && cam == Cam) return;
            Mic = mic; Cam = cam;
            Interlocked.Increment(ref Version);
        }
        catch { }
    }

    // any leaf with LastUsedTimeStop == 0 means "still in use". The value sits directly under an app
    // subkey (NonPackaged exes) OR one level deeper (packaged apps), so walk the shallow subtree.
    private static bool InUse(string capability)
    {
        using var root = Registry.CurrentUser.OpenSubKey(Base + capability);
        return root != null && AnyLive(root, 0);
    }

    // always-on background scripts (the user's controller-bridge runs under pythonw and holds the mic
    // permanently) would keep the dot lit forever — noise, not signal. Skip them.
    private static readonly string[] Ignore = { "pythonw.exe" };

    private static bool AnyLive(RegistryKey key, int depth)
    {
        if (key.GetValue("LastUsedTimeStop") is long stop && stop == 0) return true;
        if (depth >= 3) return false; // tree is shallow: capability -> app -> [NonPackaged] -> exe
        foreach (var name in key.GetSubKeyNames())
        {
            bool skip = false;
            foreach (var ig in Ignore)
                if (name.EndsWith(ig, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
            if (skip) continue;
            using var sub = key.OpenSubKey(name);
            if (sub != null && AnyLive(sub, depth + 1)) return true;
        }
        return false;
    }
}
