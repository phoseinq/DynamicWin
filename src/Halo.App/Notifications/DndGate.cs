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
    private static long _lastToast = -QuietGapMs;
    private static bool _applyPending;

    // Windows starts a toast's sound the moment the toast fires; the suppression for an app we have not
    // seen before is written afterwards, and the service restart that applies it was killing whatever was
    // still playing. Measured in notif-debug.txt: 23 restarts, every one of them ~3s after a toast, which
    // is inside most notification sounds. So the sound was neither on nor off — it started at full volume
    // and was guillotined mid-chime. Waiting for the notifications to go quiet is what makes it one or the
    // other, and 12s clears even the long ringtones (Phone Link, Teams).
    private const int QuietGapMs = 12_000;
    private const int CooldownMs = 60_000;

    // pure, so the two rules that were fighting each other can be tested without a registry or a service:
    // never restart inside a sound, and never restart more than once a minute. Both must have elapsed.
    internal static int ApplyDelayMs(long now, long lastRestart, long lastToast,
                                     int quietGap = QuietGapMs, int cooldown = CooldownMs)
        => (int)Math.Max(quietGap - (now - lastToast), Math.Max(cooldown - (now - lastRestart), 0));

    public static void Enable()
    {
        Log("enable (per-app banner suppression)");
        LoadState();                     // apps we've learned to silence across past sessions
        bool changed = false;
        lock (_lock)
            foreach (var aumid in new List<string>(_orig.Keys))
                changed |= WriteZero(aumid); // re-assert (usually already 0 → no-op → no restart)
        changed |= SeedKnownApps();          // and pre-empt everything Windows already knows about
        if (changed) ScheduleApply();        // only if something actually needed writing
    }

    // Learning one app per first toast means every app banners exactly once before we can silence it, and
    // apps that mint a fresh AUMID per account/channel (Telegram writes one per instance) leak again for
    // each new id. Windows already keeps a key under Notifications\Settings for every app that has ever
    // toasted, so claim that whole list up front: one pass, one service restart, and the long tail of rare
    // toasters (a VPN connecting, Defender, the Store) never gets its free banner. SuppressApp still
    // handles genuinely new apps afterwards, and each original value is recorded before being changed, so
    // Restore()/Uninstall() still puts every one of them back exactly as it was.
    private static bool SeedKnownApps()
    {
        bool changed = false;
        int seeded = 0;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsPath);
            if (root == null) return false;
            foreach (var aumid in Walk(root, "", 0))
            {
                lock (_lock)
                {
                    if (_orig.ContainsKey(aumid)) continue; // already learned in an earlier session
                    try { using var k = root.OpenSubKey(aumid); _orig[aumid] = k?.GetValue("ShowBanner") as int?; }
                    catch { _orig[aumid] = null; }
                    AppendState(aumid, _orig[aumid]);       // persist the ORIGINAL before we touch it
                    if (WriteZero(aumid)) { changed = true; seeded++; }
                }
            }
        }
        catch (Exception ex) { Log("seed failed: " + ex.Message); }
        if (seeded > 0) Log($"seeded {seeded} already-known app(s) from the registry");
        return changed;
    }

    // Most AUMIDs are a single key, but a classic desktop app registers as a path
    // ({GUID}\WindowsPowerShell\v1.0\powershell.exe), so the id is the whole relative path and a
    // flat GetSubKeyNames() would miss the leaf that actually carries the setting. Yield every node:
    // an intermediate one is harmless to set and Restore() removes what we added either way.
    private static IEnumerable<string> Walk(RegistryKey root, string prefix, int depth)
    {
        if (depth > 4) yield break;   // real ids are 1-4 deep; a cap keeps a corrupt hive from spinning
        string[] names;
        try { using var k = prefix.Length == 0 ? null : root.OpenSubKey(prefix); names = (k ?? root).GetSubKeyNames(); }
        catch { yield break; }
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            string full = prefix.Length == 0 ? name : prefix + "\\" + name;
            yield return full;
            foreach (var child in Walk(root, full, depth + 1)) yield return child;
        }
    }

    // called by NotifSource for every toast it mirrors: silence that app's native banner going forward
    public static void SuppressApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        bool changed;
        lock (_lock)
        {
            // called for every mirrored toast, so this is also the freshest "a sound may be playing"
            // signal there is — a burst of notifications keeps pushing a pending restart out
            _lastToast = Environment.TickCount64;
            if (!_orig.ContainsKey(aumid))
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + aumid); _orig[aumid] = k?.GetValue("ShowBanner") as int?; }
                catch { _orig[aumid] = null; }
                AppendState(aumid, _orig[aumid]); // persist the ORIGINAL before we change it, for a faithful restore
            }
            changed = WriteZero(aumid);
        }
        if (changed) ScheduleApply(); else Defer();
    }

    // nothing new to write, but a toast just landed: if a restart is already queued, hold it off so it
    // cannot land inside this one's sound
    private static void Defer()
    {
        lock (_lock)
            if (_applyPending)
                _applyTimer?.Change(ApplyDelayMs(Environment.TickCount64, _lastRestart, _lastToast),
                                    Timeout.Infinite);
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

    // WpnUserService only re-reads per-app settings on restart, so coalesce a burst of new suppressions
    // into a single restart once the notifications have gone quiet.
    private static void ScheduleApply()
    {
        lock (_lock)
        {
            _applyTimer ??= new Timer(_ => DoApply(), null, Timeout.Infinite, Timeout.Infinite);
            _applyPending = true;
            _applyTimer.Change(ApplyDelayMs(Environment.TickCount64, _lastRestart, _lastToast),
                               Timeout.Infinite);
        }
    }

    private static void DoApply()
    {
        lock (_lock)
        {
            // a toast may have landed while we waited — re-check both rules rather than assume the
            // delay we armed is still the right one
            int wait = ApplyDelayMs(Environment.TickCount64, _lastRestart, _lastToast);
            if (wait > 0) { _applyTimer?.Change(wait, Timeout.Infinite); return; }
            _lastRestart = Environment.TickCount64;
            _applyPending = false;
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
