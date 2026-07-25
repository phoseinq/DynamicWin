using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Halo.Interop;

namespace Halo.Widgets;

// Best-effort download detector. Most download managers and torrent clients put the percent at the
// START of the window title ("36% Movie.mkv", "[45%] ubuntu.iso - qBittorrent") — we poll top-level
// titles for that. Process-agnostic, no UIA, cheap (one EnumWindows/sec). Browsers are excluded: they
// title themselves with the page ("50% off - Shop") which would false-positive, and their real download
// progress lives on the taskbar with no public read API. ponytail: title-only, no speed/ETA (those sit
// in the window body → would need cross-process UIA). Upgrade path if it falls short: a browser
// extension for browsers, UIA ProgressBar scan for downloaders that only show % in the taskbar.
internal static class Downloads
{
    public static volatile string? Name;   // cleaned file/task name (or the Store app name)
    public static volatile int Percent;     // 0..100
    public static volatile string? ExePath; // downloader exe (or Store AUMID), for its icon
    public static volatile string? FilePath; // where the file is landing, when we know it (partial-file scans)
    public static volatile int OwnerPid;     // process writing that file, so we can surface it on demand
    public static volatile string? IconFile; // direct image file for the icon (GDK game's staged logo)
    public static volatile bool Installing; // Store: past download, package deploying (indeterminate)
    public static volatile bool Waiting;    // Store: queued (Pending/ReadyToDownload), download not started
    public static volatile bool Paused;     // Store: download paused
    public static volatile bool IsStore;    // this item is a Microsoft Store download/install
    public static volatile bool CanControl; // Store item is AppInstallManager-backed → pause/cancel work
    public static volatile bool NoPct;      // GDK game staging: bytes are real but total is unknown → no %
    public static long Downloaded, Total;   // Store: bytes done / total (0 when unknown)
    public static IntPtr Hwnd;              // the downloader window (Stop button reveals it)
    public static int Version;              // bumped on any change (Interlocked)

    // no cross-app API stops another program's download → the Stop button just reveals the downloader
    // so the user cancels it there (best-effort; browsers aren't detected at all, per the user).
    public static void Reveal()
    {
        var h = Hwnd;
        if (h == IntPtr.Zero) return;
        try
        {
            Win32.ShowWindow(h, Win32.SW_RESTORE); // un-minimize / show if it was tucked away
            // a bare SetForegroundWindow from our NOACTIVATE tool-window is blocked by the foreground lock
            // (target just flashes in the taskbar). Attach our input to the current foreground thread so
            // Windows lets us hand it the foreground for real.
            uint fore = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
            uint self = Win32.GetCurrentThreadId();
            bool attached = fore != 0 && fore != self && Win32.AttachThreadInput(fore, self, true);
            Win32.SetForegroundWindow(h);
            if (attached) Win32.AttachThreadInput(fore, self, false);
        }
        catch { }
    }

    // Real "stop" for a window-scanned downloader (ABDownloadManager, IDM, …): there's no per-download
    // cancel API for another app, so stop = quit the manager (its download stays paused/resumable). Kills
    // the whole process tree behind the scanned window.
    public static void StopProcess()
    {
        var h = Hwnd;
        if (h == IntPtr.Zero) return;
        try
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    // Store install control (real, via AppInstallManager). No-ops for non-Store items.
    public static void StorePause()  { if (IsStore) StoreInstall.Pause(); }
    public static void StoreResume() { if (IsStore) StoreInstall.Resume(); }
    public static void StoreCancel() { if (IsStore) StoreInstall.Cancel(); }

    // ponytail: temp diag — one line per download-state change, to learn what a "cancel doesn't work"
    // download actually is (Store MSIX = cancel via API; window-scan = only Reveal; game = no control)
    private static string _lastLog = "";
    internal static void LogState()
    {
        try
        {
            string s = $"name='{Name}' store={IsStore} canControl={CanControl} hwnd={(Hwnd != IntPtr.Zero)} exe='{ExePath}' pct={Percent} inst={Installing} wait={Waiting}";
            if (s == _lastLog) return;
            _lastLog = s;
            System.IO.File.AppendAllText(System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Halo", "dl-debug.txt"),
                $"{System.DateTime.Now:HH:mm:ss} {s}\r\n");
        }
        catch { }
    }

    private static Timer? _timer;
    private static readonly Regex Pct = new(@"^\s*\[?\s*(\d{1,3})\s*%", RegexOptions.Compiled);
    private static readonly StringBuilder Buf = new(512);
    private static readonly string[] Browsers =
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "waterfox", "librewolf" };

    public static void Poke() => _timer ??= new Timer(_ => Scan(), null, 500, 1000);

    private static void Scan()
    {
        try
        {
            string? name = null; int pct = 0; IntPtr hwnd = IntPtr.Zero;
            Win32.EnumWindows((h, _) =>
            {
                if (!Win32.IsWindowVisible(h)) return true;
                int len = Win32.GetWindowTextLengthW(h);
                if (len < 3 || len > 400) return true;
                Buf.Clear();
                if (Win32.GetWindowTextW(h, Buf, Buf.Capacity) == 0) return true;
                string t = Buf.ToString();
                var m = Pct.Match(t);
                if (!m.Success) return true;
                int p = int.Parse(m.Groups[1].Value);
                if (p >= 100) return true;        // finished / bogus → not an active download
                if (IsBrowser(h)) return true;    // "50% off" page, not a download
                name = Clean(t, m); pct = p; hwnd = h;
                return false;                     // first match (topmost in Z-order) wins
            }, IntPtr.Zero);

            if (name == null)
            {
                // no window-title download → a Microsoft Store download/install (real state, %, bytes,
                // and control via the Store's own AppInstallManager — see StoreInstall).
                var ph = StoreInstall.Poll(out string app, out int spct, out long done, out long total);
                if (ph != StoreInstall.Phase.None)
                {
                    bool installing = ph == StoreInstall.Phase.Installing;
                    bool waiting = ph == StoreInstall.Phase.Waiting;
                    bool paused = ph == StoreInstall.Phase.Paused;
                    IconFile = null;
                    if (!IsStore) ExePath = StoreAumid; // Store tile icon (resolved via ShellIcon in the widget)
                    if (!IsStore || Name != app || Percent != spct || Installing != installing
                        || Waiting != waiting || Paused != paused || Downloaded != done || Total != total)
                    {
                        Name = app; Percent = spct; Installing = installing; Waiting = waiting; Paused = paused; FilePath = null; OwnerPid = 0;
                        Downloaded = done; Total = total; IsStore = true; CanControl = true; NoPct = false;
                        Hwnd = IntPtr.Zero; Interlocked.Increment(ref Version); LogState();
                    }
                    return;
                }
                // GDK games (Roblox & co.) install through Gaming Services — invisible to BOTH the
                // install queue and Delivery Optimization. Their staging folder on disk is the signal:
                // live bytes from folder growth, total from the Store catalog → a real filling %. If the
                // catalog total isn't in yet, NoPct → spinner + live MB until it arrives.
                if (GameInstall.Poll(out string gApp, out long gDone, out long gTotal, out bool gStalled))
                {
                    int gPct = gTotal > 0 ? (int)Math.Clamp(gDone * 100 / gTotal, 0, 99) : 0;
                    bool gNoPct = gTotal <= 0;
                    IconFile = GameInstall.LogoPath;      // show the game's own logo, not the Store bag
                    if (!IsStore) ExePath = StoreAumid;
                    if (!IsStore || Name != gApp || Downloaded != gDone || Total != gTotal
                        || Percent != gPct || Paused != gStalled || NoPct != gNoPct)
                    {
                        Name = gApp; Percent = gPct; Installing = false; Waiting = false; Paused = gStalled; FilePath = null; OwnerPid = 0;
                        Downloaded = gDone; Total = gTotal; IsStore = true; CanControl = false; NoPct = gNoPct;
                        Hwnd = IntPtr.Zero; Interlocked.Increment(ref Version); LogState();
                    }
                    return;
                }
                // Steam keeps no percentage in its window title, so the title scan above is blind to it.
                // Its manifests carry real byte counts — see SteamInstall.
                if (SteamInstall.Current() is { } steam)
                {
                    int sPct = (int)Math.Clamp(steam.Done * 100 / Math.Max(steam.Total, 1), 0, 99);
                    if (Name != steam.Name || Downloaded != steam.Done || Total != steam.Total || Percent != sPct || IsStore)
                    {
                        Name = steam.Name; Percent = sPct; Installing = false; Waiting = false; Paused = false; FilePath = null; OwnerPid = 0;
                        Downloaded = steam.Done; Total = steam.Total; IsStore = false; CanControl = false; NoPct = false;
                        ExePath = SteamExe(); IconFile = null; Hwnd = IntPtr.Zero;
                        Interlocked.Increment(ref Version); LogState();
                    }
                    return;
                }
                // Last and most general: a partial file growing on disk. This is what finally covers
                // browsers (skipped above on purpose, since a page title can read "50% off") and any other
                // app that downloads. The filesystem knows the bytes but not the total, so ask the
                // browser's own database for it; when nothing supplies a total, NoPct keeps the widget
                // honest — live bytes and a breathing bar instead of a made-up percentage.
                if (PartialFiles.Current() is { } part)
                {
                    long pTotal = BrowserDownloads.TotalFor(part.Path);
                    string label = part.Name.Length > 0 ? part.Name
                        : BrowserDownloads.NameFor(part.Path)
                          ?? Downloaders.AppFor(System.IO.Path.GetDirectoryName(part.Path))
                          ?? "Downloading";
                    bool noPct = pTotal <= part.Bytes;  // unknown or already passed → don't pretend
                    int pPct = noPct ? 0 : (int)Math.Clamp(part.Bytes * 100 / pTotal, 0, 99);
                    if (Name != label || Downloaded != part.Bytes || Total != (noPct ? 0 : pTotal)
                        || Percent != pPct || NoPct != noPct || IsStore)
                    {
                        Name = label; Percent = pPct; Installing = false; Waiting = false; Paused = false;
                        Downloaded = part.Bytes; Total = noPct ? 0 : pTotal; IsStore = false; CanControl = false;
                        NoPct = noPct; IconFile = null; Hwnd = IntPtr.Zero;
                        FilePath = part.Path; OwnerPid = part.OwnerPid;
                        ExePath = part.OwnerPid != 0 ? ExeOfPid(part.OwnerPid) : null;
                        Interlocked.Increment(ref Version); LogState();
                    }
                    return;
                }
                if (Name != null)
                {
                    Name = null; Percent = 0; ExePath = null; IconFile = null; Installing = false; Waiting = false; Paused = false;
                    IsStore = false; CanControl = false; NoPct = false; Downloaded = Total = 0; Hwnd = IntPtr.Zero;
                    FilePath = null; OwnerPid = 0;
                    Interlocked.Increment(ref Version);
                }
                return;
            }
            Hwnd = hwnd; // keep fresh even when only the % moves (the window can be recreated)
            if (name != Name || pct != Percent || IsStore)
            {
                if (name != Name || IsStore) ExePath = ExeOf(hwnd); // resolve the icon only when the task changes
                Name = name; Percent = pct; Installing = false; Waiting = false; Paused = false; IconFile = null; FilePath = null; OwnerPid = 0;
                IsStore = false; CanControl = false; NoPct = false; Downloaded = Total = 0;
                Interlocked.Increment(ref Version); LogState();
            }
        }
        catch { }
    }

    // Select the file in Explorer. For a partial download that means the .crdownload, which is still the
    // right place to land: the folder is what the user wants, and the file appears there when it finishes.
    public static void ShowInFolder()
    {
        var path = FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch { }
    }

    // Bring the app doing the downloading to the front so the user can cancel it there. No cross-app API
    // stops another program's download, and for a browser the only "stop" we could implement ourselves
    // would be killing the browser or deleting the half-written file — both worse than the problem.
    public static void RevealOwner()
    {
        int pid = OwnerPid;
        if (pid == 0) { Reveal(); return; }
        try
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((h, _) =>
            {
                if (!Win32.IsWindowVisible(h)) return true;
                Win32.GetWindowThreadProcessId(h, out uint wp);
                if (wp != (uint)pid || Win32.GetWindowTextLengthW(h) < 1) return true;
                found = h; return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return;
            Win32.ShowWindow(found, Win32.SW_RESTORE);
            uint fore = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
            uint self = Win32.GetCurrentThreadId();
            bool attached = fore != 0 && fore != self && Win32.AttachThreadInput(fore, self, true);
            Win32.SetForegroundWindow(found);
            if (attached) Win32.AttachThreadInput(fore, self, false);
        }
        catch { }
    }

    private const string StoreAumid = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";

    // icon source for a partial-file download: the exe of whichever process is writing the file
    private static string? ExeOfPid(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.MainModule?.FileName; }
        catch { return null; }
    }

    private static string? SteamExe()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            string? dir = k?.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(dir)) return null;
            string exe = System.IO.Path.Combine(System.IO.Path.GetFullPath(dir!.Replace('/', '\\')), "steam.exe");
            return System.IO.File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    private static bool IsBrowser(IntPtr h)
    {
        try
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            string pn = p.ProcessName.ToLowerInvariant();
            foreach (var b in Browsers) if (pn.Contains(b)) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? ExeOf(IntPtr h)
    {
        try
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName;
        }
        catch { return null; }
    }

    // strip the leading "36%" / "[45%]" and any following separator; keep the rest as the task name
    private static string Clean(string title, Match m)
    {
        string s = title.Substring(m.Index + m.Length).TrimStart(']', ' ', '-', ':', '\t', '|', '»');
        return s.Length == 0 ? title.Trim() : s;
    }
}
