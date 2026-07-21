using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Halo.Notifications;

// Halo mirrors every toast in the pill, so Windows' own slide-in banner is a duplicate. On Win11 26200 there
// is NO supported or live way to engage global Do-Not-Disturb — the DND on/off state is a volatile WNF value
// with no registry backing and no API (verified: writing the QuietHoursProfile / the Focus-Assist WNF / a
// scheduled rule never enters quiet-time, and toggling DND changes nothing on disk). What DOES work is
// Windows' own per-app switch: ShowBanner=0 under the app's Notifications\Settings key kills that app's banner
// while the toast STILL lands in the Action Center, so UserNotificationListener keeps delivering it to Halo.
//
// The catch: WpnUserService only reads per-app settings at ITS OWN startup, not live. So after writing
// ShowBanner=0 for a newly-seen app we restart the service (debounced + rate-limited) to make it take effect
// this session; at every logon the service starts fresh and reads all persisted values on its own, so known
// apps are silent from the first toast. Restarting the service invalidates our listener — NotifSource
// self-heals it (see NotifSource.Reheal). Each app's ORIGINAL ShowBanner is saved to disk before we touch it,
// so a clean Restore()/Uninstall() puts it back exactly; we only ever change apps we suppressed.
internal static class BannerGate
{
    private const string SettingsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int?> _orig = new(StringComparer.OrdinalIgnoreCase); // aumid → original ShowBanner (null = value was absent)

    private static readonly string HaloDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");
    private static readonly string StatePath = Path.Combine(HaloDir, "banner-orig.tsv"); // learned apps + their original values
    private static readonly string DebugPath = Path.Combine(HaloDir, "notif-debug.txt");
    private static void Log(string m) { try { File.AppendAllText(DebugPath, $"{DateTime.Now:HH:mm:ss} [banner] {m}\r\n"); } catch { } }

    private static Timer? _applyTimer;
    private static long _lastRestart = -60_000;

    public static void Enable()
    {
        Log("enable (per-app banner suppression)");
        LoadState();                     // apps we've learned to silence across past sessions
        bool changed = false;
        lock (_lock)
            foreach (var aumid in new List<string>(_orig.Keys))
                changed |= WriteZero(aumid); // re-assert (usually already 0 → no-op → no restart)
        if (changed) ScheduleApply();        // only if something had reset them
    }

    // called by NotifSource for every toast it mirrors: silence that app's native banner going forward
    public static void SuppressApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        bool changed;
        lock (_lock)
        {
            if (!_orig.ContainsKey(aumid))
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + aumid); _orig[aumid] = k?.GetValue("ShowBanner") as int?; }
                catch { _orig[aumid] = null; }
                AppendState(aumid, _orig[aumid]); // persist the ORIGINAL before we change it, for a faithful restore
            }
            changed = WriteZero(aumid);
        }
        if (changed) ScheduleApply();
    }

    // silence the banner, the sound, AND urgent break-through — Windows treats these as independent per-app
    // toggles: ShowBanner=0 alone still lets the sound play, and an app that marks a toast "urgent"
    // (Phone Link calls/messages) still banners over ShowBanner=0 unless AllowUrgentNotifications=0 too.
    // The toast still lands in the Action Center → the listener still delivers it → Halo still mirrors it.
    // Returns true only if a value actually changed (→ a service restart is worth it).
    private static readonly string[] SilenceKeys = { "ShowBanner", "Sound", "AllowUrgentNotifications" };
    private static bool WriteZero(string aumid)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsPath + "\\" + aumid, writable: true);
            if (k == null) return false;
            bool changed = false;
            foreach (var name in SilenceKeys)
                if ((k.GetValue(name) as int?) != 0) { k.SetValue(name, 0, RegistryValueKind.DWord); changed = true; }
            if (changed) Log($"silenced (banner+sound+urgent) → {aumid}");
            return changed;
        }
        catch (Exception ex) { Log($"suppress {aumid} failed: {ex.Message}"); return false; }
    }

    // WpnUserService only re-reads per-app settings on restart, so coalesce a burst of new suppressions into a
    // single restart ~3s after the last one, and never restart more than once per 60s.
    private static void ScheduleApply()
    {
        lock (_lock)
        {
            _applyTimer ??= new Timer(_ => DoApply(), null, Timeout.Infinite, Timeout.Infinite);
            _applyTimer.Change(3_000, Timeout.Infinite);
        }
    }

    private static void DoApply()
    {
        lock (_lock)
        {
            long since = Environment.TickCount64 - _lastRestart;
            if (since < 60_000) { _applyTimer?.Change(60_000 - since, Timeout.Infinite); return; } // cooldown → defer
            _lastRestart = Environment.TickCount64;
        }
        Log("applying → WpnUserService restart (listener self-heals)");
        RestartService();
    }

    private static void RestartService()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"Restart-Service -Name 'WpnUserService_*' -Force\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex) { Log("restart failed: " + ex.Message); }
    }

    // put every app's banner back exactly as we found it. Registry only — the service picks it up at next
    // logon; pair with a RestartService() (Uninstall does) to apply it now.
    public static void Restore()
    {
        lock (_lock)
        {
            foreach (var (aumid, prior) in _orig)
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + aumid, writable: true);
                    if (k == null) continue;
                    if (prior is int p) k.SetValue("ShowBanner", p, RegistryValueKind.DWord); // the user's own value
                    else k.DeleteValue("ShowBanner", throwOnMissingValue: false);             // one we introduced → remove
                    // ponytail: we only ever add Sound=0 / AllowUrgentNotifications=0; a user's own prefs for
                    // these aren't tracked, so restore = remove them (back to Windows defaults)
                    k.DeleteValue("Sound", throwOnMissingValue: false);
                    k.DeleteValue("AllowUrgentNotifications", throwOnMissingValue: false);
                }
                catch { }
            }
            Log("restored native banners");
        }
    }

    // full teardown for disable/uninstall (Program.cs --restore-notifications): restore, push it live with a
    // restart, and forget the learned set so a later run starts clean.
    public static void Uninstall()
    {
        LoadState();  // the --restore-notifications CLI is a fresh process that never called Enable(), so
        Restore();    // populate _orig from disk before restoring, or we'd restore nothing
        RestartService();
        try { File.Delete(StatePath); } catch { }
        Log("uninstall: restored + cleared state");
    }

    private static void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            foreach (var line in File.ReadAllLines(StatePath))
            {
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                _orig[line.Substring(0, tab)] = int.TryParse(line.Substring(tab + 1), out var n) ? n : (int?)null;
            }
            Log($"loaded {_orig.Count} learned app(s)");
        }
        catch (Exception ex) { Log("load state failed: " + ex.Message); }
    }

    private static void AppendState(string aumid, int? orig)
    {
        try { Directory.CreateDirectory(HaloDir); File.AppendAllText(StatePath, $"{aumid}\t{orig?.ToString() ?? ""}\r\n"); }
        catch { }
    }
}
