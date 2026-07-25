using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Halo.Widgets;

// Universal download detector: watch the FILESYSTEM instead of the app. Every browser and most
// downloaders write to a partial file first (.crdownload, .part, …) and rename it on completion, so a
// partial file whose length keeps growing IS a download in flight — no per-app integration, no window
// title guessing. This is what finally covers browsers, which Downloads.Scan deliberately skips because
// a page title can read "50% off".
//
// What the filesystem cannot tell us is the FINAL size, so this reports bytes-so-far and leaves the
// percentage to BrowserDownloads (which reads the browser's own total). Unknown total → the widget shows
// its indeterminate breathing state rather than a made-up number.
internal static class PartialFiles
{
    // Longest suffix first so ".crdownload" isn't mistaken for a generic ".download".
    private static readonly string[] Suffixes =
        { ".crdownload", ".opdownload", ".partial", ".download", ".aria2", ".part", ".!ut", ".!qb" };

    private const long MinSize = 128 * 1024;          // below this it's a scratch file, not a download
    private const int StaleSeconds = 20;              // untouched for longer → finished or abandoned

    internal readonly record struct Sample(string Path, string Name, long Bytes, long GrowthPerSec, int OwnerPid, bool Stalled);

    // Two consecutive samples with no growth. Reading the file's LastWriteTime instead was slow to react:
    // Windows does not flush that timestamp on every write, so a stopped download kept looking alive for
    // several seconds after it had actually stopped. Comparing the length we already read is immediate.
    private const int StallSamples = 2;

    // path → (bytes, when) from the previous scan, so growth is measured rather than assumed
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
                // Chrome names the partial "Unconfirmed 123456.crdownload" — no real name to show,
                // so leave it empty and let the caller fall back to the browser's own record.
                if (cleanName.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) cleanName = "";
                return true;
            }
        return false;
    }

    // The roots worth scanning: the user's Downloads folder plus anything learned. Temp is deliberately
    // NOT scanned wholesale — it churns constantly and a partial file there is usually an installer's
    // own business, not a user-visible download.
    private static IEnumerable<string> Roots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        foreach (var d in Downloaders.Directories()) yield return d;
    }

    // The best in-flight download right now, or null. "Best" = fastest growing, so a big active download
    // wins over a stalled leftover.
    // How many partial downloads the last scan saw. The pill still shows only the busiest one; this is
    // what a switcher will need, and it lets the panel reserve its gutter today instead of the layout
    // shifting the first time a second download appears.
    public static int LiveCount { get; private set; }

    public static Sample? Current()
    {
        Sample? best = null;
        int seen = 0;
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

                    live.Add(path);
                    seen++;
                    // Growth is only used to RANK candidates, never to decide whether this is a download.
                    // Requiring fresh growth every sample made the pill vanish the moment a download
                    // stalled for a second (observed live: Chrome's file was found, then dropped one second
                    // later). Freshness is already the liveness test — StaleSeconds above — so a paused but
                    // recently-touched partial file stays on the pill instead of flickering out.
                    long rate = 0;
                    int flat = 0;
                    if (_seen.TryGetValue(path, out var prev))
                    {
                        double secs = (now - prev.at).TotalSeconds;
                        if (secs >= 0.5)
                        {
                            long grew = len - prev.bytes;
                            if (grew > 0) rate = (long)(grew / secs);
                            flat = grew > 0 ? 0 : prev.flat + 1;   // count samples that saw no new bytes
                            _seen[path] = (len, now, flat);
                        }
                        else { flat = prev.flat; rate = prev.bytes == len ? 0 : 1; } // too soon to measure
                    }
                    else _seen[path] = (len, now, 0);

                    // "not growing right now" is not "abandoned": the file is still fresh enough to count as
                    // a live download (StaleSeconds above), it is just sitting still — which is the paused
                    // state the pill should mark on the icon instead of pretending it is moving.
                    bool stalled = flat >= StallSamples;
                    if (best is null || rate > best.Value.GrowthPerSec)
                        best = new Sample(path, clean, len, rate, 0, stalled);
                }
            }
            // forget files that vanished (renamed on completion) so the dictionary can't grow forever
            if (_seen.Count > 64)
                foreach (var k in new List<string>(_seen.Keys))
                    if (!live.Contains(k)) _seen.Remove(k);
        }
        catch { }

        LiveCount = seen;
        if (best is null) return null;
        int pid = OwnerPid(best.Value.Path);
        if (pid != 0) Downloaders.Learn(pid, Path.GetDirectoryName(best.Value.Path));
        return best.Value with { OwnerPid = pid };
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly); }
        catch { return Array.Empty<string>(); }
    }

    // ── which process is writing this file ────────────────────────────────────────────────────────
    // Restart Manager is the documented, non-admin way to ask "who holds this file". Verified live: it
    // named the holder correctly while unelevated. The alternative (NtQuerySystemInformation handle
    // enumeration) needs more privilege and far more interop for the same answer.
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
                if (pid != 0 && pid != Environment.ProcessId) return pid; // never blame ourselves
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
