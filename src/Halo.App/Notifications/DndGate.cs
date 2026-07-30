using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace Halo.Notifications;

internal static class BannerGate
{
    private const string SettingsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int?> _orig = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string HaloDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");
    private static readonly string StatePath = Path.Combine(HaloDir, "banner-orig.tsv");
    private static readonly string DebugPath = Path.Combine(HaloDir, "notif-debug.txt");
    private static void Log(string m) { try { File.AppendAllText(DebugPath, $"{DateTime.Now:HH:mm:ss} [banner] {m}\r\n"); } catch { } }

    private static Timer? _applyTimer;
    private static long _lastRestart = -60_000;
    private static long _lastToast = -QuietGapMs;
    private static bool _applyPending;
    private static long _applySince;

    private const int QuietGapMs = 12_000;
    private const int CooldownMs = 60_000;
    private const int MaxDeferMs = 30_000;

    internal static int ApplyDelayMs(long now, long lastRestart, long lastToast,
                                     int quietGap = QuietGapMs, int cooldown = CooldownMs,
                                     long pendingSince = 0, int maxDefer = MaxDeferMs)
    {
        long quiet = pendingSince > 0 && now - pendingSince >= maxDefer ? 0 : quietGap - (now - lastToast);
        return (int)Math.Max(quiet, Math.Max(cooldown - (now - lastRestart), 0));
    }

    public static void Enable()
    {
        Log("enable (per-app banner suppression)");
        LoadState();
        lock (_lock)
        {

            foreach (var aumid in new List<string>(_orig.Keys))
                if (aumid != GlobalKey) WriteZero(aumid);
            SilenceGlobalSound();
        }
        SeedKnownApps();

        ScheduleApply();
    }

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
                    if (_orig.ContainsKey(aumid)) continue;
                    try { using var k = root.OpenSubKey(aumid); _orig[aumid] = k?.GetValue("ShowBanner") as int?; }
                    catch { _orig[aumid] = null; }
                    AppendState(aumid, _orig[aumid]);
                    if (WriteZero(aumid)) { changed = true; seeded++; }
                }
            }
        }
        catch (Exception ex) { Log("seed failed: " + ex.Message); }
        if (seeded > 0) Log($"seeded {seeded} already-known app(s) from the registry");
        return changed;
    }

    private static IEnumerable<string> Walk(RegistryKey root, string prefix, int depth)
    {
        if (depth > 4) yield break;
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

    public static void SuppressApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return;
        bool changed;
        lock (_lock)
        {

            _lastToast = Environment.TickCount64;
            if (!_orig.ContainsKey(aumid))
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + aumid); _orig[aumid] = k?.GetValue("ShowBanner") as int?; }
                catch { _orig[aumid] = null; }
                AppendState(aumid, _orig[aumid]);
            }
            changed = WriteZero(aumid);
        }
        if (changed) ScheduleApply(); else Defer();
    }

    private static void Defer()
    {
        lock (_lock)
            if (_applyPending)
                _applyTimer?.Change(ApplyDelayMs(Environment.TickCount64, _lastRestart, _lastToast,
                                                 pendingSince: _applySince),
                                    Timeout.Infinite);
    }

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

    private const string GlobalSoundValue = "NOC_GLOBAL_SETTING_ALLOW_NOTIFICATION_SOUND";
    private const string GlobalKey = "global";

    private static bool SilenceGlobalSound()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsPath, writable: true);
            if (k == null) return false;
            var now = k.GetValue(GlobalSoundValue) as int?;
            if (now == 0) return false;
            if (!_orig.ContainsKey(GlobalKey))
            {
                _orig[GlobalKey] = now;
                AppendState(GlobalKey, now);
            }
            k.SetValue(GlobalSoundValue, 0, RegistryValueKind.DWord);
            Log($"silenced global notification sound (was {now?.ToString() ?? "unset"})");
            return true;
        }
        catch (Exception ex) { Log("global sound off failed: " + ex.Message); return false; }
    }

    private static void RestoreGlobalSound()
    {
        if (!_orig.TryGetValue(GlobalKey, out var prior)) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsPath, writable: true);
            if (k == null) return;
            if (prior is int p) k.SetValue(GlobalSoundValue, p, RegistryValueKind.DWord);
            else k.DeleteValue(GlobalSoundValue, throwOnMissingValue: false);
            Log("restored global notification sound");
        }
        catch { }
    }

    private static void ScheduleApply()
    {
        lock (_lock)
        {
            _applyTimer ??= new Timer(_ => DoApply(), null, Timeout.Infinite, Timeout.Infinite);
            if (!_applyPending) _applySince = Environment.TickCount64;
            _applyPending = true;
            _applyTimer.Change(ApplyDelayMs(Environment.TickCount64, _lastRestart, _lastToast,
                                            pendingSince: _applySince),
                               Timeout.Infinite);
        }
    }

    private static void DoApply()
    {
        lock (_lock)
        {

            int wait = ApplyDelayMs(Environment.TickCount64, _lastRestart, _lastToast,
                                    pendingSince: _applySince);
            if (wait > 0) { _applyTimer?.Change(wait, Timeout.Infinite); return; }
            _lastRestart = Environment.TickCount64;
            _applyPending = false;
            _applySince = 0;
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

    public static void Restore()
    {
        lock (_lock)
        {
            RestoreGlobalSound();
            foreach (var (aumid, prior) in _orig)
            {
                if (aumid == GlobalKey) continue;
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(SettingsPath + "\\" + aumid, writable: true);
                    if (k == null) continue;
                    if (prior is int p) k.SetValue("ShowBanner", p, RegistryValueKind.DWord);
                    else k.DeleteValue("ShowBanner", throwOnMissingValue: false);

                    k.DeleteValue("Sound", throwOnMissingValue: false);
                    k.DeleteValue("AllowUrgentNotifications", throwOnMissingValue: false);
                }
                catch { }
            }
            Log("restored native banners");
        }
    }

    public static void Uninstall()
    {
        LoadState();
        Restore();
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
