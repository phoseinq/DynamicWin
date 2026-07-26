using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Halo.Widgets;

internal static class PartialFiles
{

    private static readonly string[] Suffixes =
        { ".crdownload", ".opdownload", ".partial", ".download", ".aria2", ".part", ".!ut", ".!qb" };

    private const long MinSize = 128 * 1024;
    private const int StaleSeconds = 20;

    internal readonly record struct Sample(string Path, string Name, long Bytes, long GrowthPerSec, int OwnerPid, bool Stalled);

    private const int StallSamples = 2;

    private static readonly Dictionary<string, (long bytes, DateTime at, int flat)> _seen =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPartial(string path, out string cleanName)
    {
        cleanName = "";
        if (string.IsNullOrEmpty(path)) return false;
        string file = Path.GetFileName(path);
        foreach (var s in Suffixes)
            if (file.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                cleanName = file.Substring(0, file.Length - s.Length);

                if (cleanName.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) cleanName = "";
                return true;
            }
        return false;
    }

    private static IEnumerable<string> Roots()
    {

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Prepend(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            string full;
            try { full = Path.GetFullPath(d).TrimEnd('\\'); } catch { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    private static IEnumerable<string> Prepend(string profile)
    {
        yield return Path.Combine(profile, "Downloads");
        foreach (var d in Downloaders.Directories()) yield return d;
    }

    public static int LiveCount { get; private set; }

    public static Sample[] All()
    {
        var found = new List<Sample>();
        var now = DateTime.UtcNow;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var root in Roots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (var path in Enumerate(root))
                {
                    if (!IsPartial(path, out string clean)) continue;
                    long len; DateTime touched;
                    try { var fi = new FileInfo(path); len = fi.Length; touched = fi.LastWriteTimeUtc; }
                    catch { continue; }
                    if (len < MinSize) continue;
                    if ((now - touched).TotalSeconds > StaleSeconds) continue;

                    if (!live.Add(path)) continue;

                    long rate = 0;
                    int flat = 0;
                    if (_seen.TryGetValue(path, out var prev))
                    {
                        double secs = (now - prev.at).TotalSeconds;
                        if (secs >= 0.5)
                        {
                            long grew = len - prev.bytes;
                            if (grew > 0) rate = (long)(grew / secs);
                            flat = grew > 0 ? 0 : prev.flat + 1;
                            _seen[path] = (len, now, flat);
                        }
                        else { flat = prev.flat; rate = prev.bytes == len ? 0 : 1; }
                    }
                    else _seen[path] = (len, now, 0);

                    int pid = OwnerPid(path);
                    if (pid != 0) Downloaders.Learn(pid, Path.GetDirectoryName(path));
                    found.Add(new Sample(path, clean, len, rate, pid, flat >= StallSamples));
                }
            }

            if (_seen.Count > 64)
                foreach (var k in new List<string>(_seen.Keys))
                    if (!live.Contains(k)) _seen.Remove(k);
        }
        catch { }

        LiveCount = found.Count;
        return found.ToArray();
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    public static int OwnerPid(string path)
    {
        uint session = 0;
        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        try
        {
            if (RmStartSession(out session, 0, key) != 0) return 0;
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0) return 0;
            uint count = 0;
            int rc = RmGetList(session, out uint needed, ref count, null, out _);
            if (needed == 0 || (rc != 0 && rc != ERROR_MORE_DATA)) return 0;
            var infos = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, infos, out _) != 0) return 0;
            for (int i = 0; i < count; i++)
            {
                int pid = (int)infos[i].Process.dwProcessId;
                if (pid != 0 && pid != Environment.ProcessId) return pid;
            }
            return 0;
        }
        catch { return 0; }
        finally { if (session != 0) { try { RmEndSession(session); } catch { } } }
    }

    private const int CCH_RM_SESSION_KEY = 32, ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public uint dwProcessId; public FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, IntPtr rgApplications, uint nServices, string[]? rgsServiceNames);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);
}
