using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Halo.Notifications;

internal static class DndGate
{
    private const string Ns = "Microsoft.QuietHoursProfile.";
    private const string Block = "AlarmsOnly";
    private const string Off = "Unrestricted";
    private const string CurrentPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";
    private const string CachePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\Store\Cache\DefaultAccount";

    private static System.Threading.Timer? _watch;

    public static void Enable()
    {
        SetProfile(Block);

        _watch = new System.Threading.Timer(_ =>
        {
            SetProfile(Block);
            if (!CacheAgrees()) RestartNotificationService();
        }, null, 20_000, 20_000);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { _watch?.Dispose(); SetProfile(Off); };
    }

    private static bool CacheAgrees()
    {
        try
        {
            using var y = OpenCache(writable: false);
            if (y?.GetValue("Data") is not byte[] data) return true;
            string s = Encoding.Unicode.GetString(data);
            return s.Contains(Ns + Block, StringComparison.OrdinalIgnoreCase)
                || !s.Contains(Ns, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static void SetProfile(string profile)
    {
        bool changed = false;
        try
        {
            using var y = OpenSettings(writable: true);
            if (y?.GetValue("Data") is byte[] cur && cur.Length >= 31)
            {
                int oldChars = cur[28];
                int trailerAt = 29 + oldChars * 2;
                if (trailerAt <= cur.Length)
                {
                    var name = Encoding.Unicode.GetBytes(Ns + profile);
                    var neu = new byte[29 + name.Length + (cur.Length - trailerAt)];
                    Array.Copy(cur, 0, neu, 0, 29);
                    neu[28] = (byte)(name.Length / 2);
                    Array.Copy(name, 0, neu, 29, name.Length);
                    Array.Copy(cur, trailerAt, neu, 29 + name.Length, cur.Length - trailerAt);
                    if (!neu.SequenceEqual(cur)) { y.SetValue("Data", neu, RegistryValueKind.Binary); changed = true; }
                }
            }
        }
        catch { }

        if (WriteCache(profile)) changed = true;
        if (changed) RestartNotificationService();
    }

    private static bool WriteCache(string profile)
    {
        try
        {
            using var y = OpenCache(writable: true);
            if (y?.GetValue("Data") is not byte[] d || d.Length < 32) return false;
            var needle = Encoding.Unicode.GetBytes(Ns);
            int idx = Find(d, needle);
            if (idx < 1) return false;
            int cnt = d[idx - 1];
            if (idx + cnt * 2 > d.Length) return false;
            var name = Encoding.Unicode.GetBytes(Ns + profile);
            int trailerLen = d.Length - (idx + cnt * 2);
            var neu = new byte[idx + name.Length + trailerLen];
            Array.Copy(d, 0, neu, 0, idx);
            neu[idx - 1] = (byte)(name.Length / 2);
            Array.Copy(name, 0, neu, idx, name.Length);
            Array.Copy(d, idx + cnt * 2, neu, idx + name.Length, trailerLen);
            bool nameChanged = !neu.SequenceEqual(d);
            var ft = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Array.Copy(ft, 0, neu, 4, 8);
            y.SetValue("Data", neu, RegistryValueKind.Binary);
            return nameChanged;
        }
        catch { return false; }
    }

    private static int Find(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static RegistryKey? OpenCache(bool writable)
    {
        using var acc = Registry.CurrentUser.OpenSubKey(CachePath);
        var xName = acc?.GetSubKeyNames()
            .FirstOrDefault(n => n.EndsWith("$windows.data.notifications.quiethourssettings", StringComparison.OrdinalIgnoreCase));
        if (xName == null) return null;
        using var x = acc!.OpenSubKey(xName);
        var yName = x?.GetSubKeyNames().FirstOrDefault();
        return yName == null ? null : x!.OpenSubKey(yName, writable);
    }

    private static RegistryKey? OpenSettings(bool writable)
    {
        using var current = Registry.CurrentUser.OpenSubKey(CurrentPath);
        var xName = current?.GetSubKeyNames()
            .FirstOrDefault(n => n.EndsWith("$windows.data.donotdisturb.quiethourssettings", StringComparison.OrdinalIgnoreCase));
        if (xName == null) return null;
        using var x = current!.OpenSubKey(xName);
        var yName = x?.GetSubKeyNames().FirstOrDefault();
        return yName == null ? null : x!.OpenSubKey(yName, writable);
    }

    private static void RestartNotificationService()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -WindowStyle Hidden -Command \"Restart-Service -Name 'WpnUserService_*' -Force\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch { }
    }
}
