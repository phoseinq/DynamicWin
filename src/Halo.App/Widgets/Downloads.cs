using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Halo.Interop;

namespace Halo.Widgets;

internal static class Downloads
{
    public static volatile string? Name;
    public static volatile int Percent;
    public static volatile string? ExePath;
    public static volatile string? FilePath;
    public static volatile int OwnerPid;

    public static volatile int Count;
    public static bool HasMore => Count > 1;
    public static volatile string? IconFile;
    public static volatile bool Installing;
    public static volatile bool Waiting;
    public static volatile bool Paused;
    public static volatile bool IsStore;
    public static volatile bool CanControl;
    public static volatile bool NoPct;

    public static volatile bool NoBytes;
    public static long Downloaded, Total;
    public static IntPtr Hwnd;
    public static int Version;

    public static void Reveal()
    {
        var h = Hwnd;
        if (h == IntPtr.Zero) return;
        try
        {
            Win32.ShowWindow(h, Win32.SW_RESTORE);

            uint fore = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
            uint self = Win32.GetCurrentThreadId();
            bool attached = fore != 0 && fore != self && Win32.AttachThreadInput(fore, self, true);
            Win32.SetForegroundWindow(h);
            if (attached) Win32.AttachThreadInput(fore, self, false);
        }
        catch { }
    }

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

    public static void StorePause()  { if (IsStore) StoreInstall.Pause(); }
    public static void StoreResume() { if (IsStore) StoreInstall.Resume(); }
    public static void StoreCancel() { if (IsStore) StoreInstall.Cancel(); }

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

    internal sealed record DlItem(string Key, string Name, int Percent, long Downloaded, long Total,
        bool NoPct, bool NoBytes, bool Paused, bool Installing, bool Waiting, bool IsStore, bool CanControl,
        string? ExePath, string? IconFile, string? FilePath, int OwnerPid, IntPtr Hwnd);

    private static DlItem[] _items = Array.Empty<DlItem>();
    public static IReadOnlyList<DlItem> Items => _items;

    private static readonly Dictionary<string, long> _born = new(StringComparer.Ordinal);
    private static string? _selKey;
    private static DlItem? _applied;

    public static int SelectedIndex
    {
        get { var a = _items; for (int i = 0; i < a.Length; i++) if (a[i].Key == _selKey) return i; return 0; }
    }

    public static void Select(int index)
    {
        var a = _items;
        if (index < 0 || index >= a.Length) return;
        _selKey = a[index].Key;
        Apply(a[index]);
    }

    internal static void Order(List<DlItem> found, Dictionary<string, long> born, long now)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in found) { keys.Add(i.Key); if (!born.ContainsKey(i.Key)) born[i.Key] = now; }
        foreach (var k in new List<string>(born.Keys)) if (!keys.Contains(k)) born.Remove(k);

        found.Sort((a, b) => born[a.Key] != born[b.Key]
            ? born[a.Key].CompareTo(born[b.Key]) : string.CompareOrdinal(a.Key, b.Key));
    }

    private static void Publish(List<DlItem> found)
    {
        Order(found, _born, Environment.TickCount64);
        _items = found.ToArray();
        Count = _items.Length;
        Apply(Pick());
    }

    private static DlItem? Pick()
    {
        var a = _items;
        if (a.Length == 0) { _selKey = null; return null; }
        if (_selKey != null) foreach (var i in a) if (i.Key == _selKey) return i;
        _selKey = null;
        return a[0];
    }

    private static void Apply(DlItem? it)
    {
        if (it == _applied) return;
        _applied = it;
        if (it is null)
        {
            Name = null; Percent = 0; ExePath = null; IconFile = null; Installing = false; Waiting = false;
            Paused = false; IsStore = false; CanControl = false; NoPct = false; NoBytes = false; Downloaded = Total = 0;
            Hwnd = IntPtr.Zero; FilePath = null; OwnerPid = 0;
            Interlocked.Increment(ref Version);
            return;
        }
        Name = it.Name; Percent = it.Percent; Downloaded = it.Downloaded; Total = it.Total;
        NoPct = it.NoPct; NoBytes = it.NoBytes; Paused = it.Paused; Installing = it.Installing; Waiting = it.Waiting;
        IsStore = it.IsStore; CanControl = it.CanControl; ExePath = it.ExePath; IconFile = it.IconFile;
        FilePath = it.FilePath; OwnerPid = it.OwnerPid; Hwnd = it.Hwnd;
        Interlocked.Increment(ref Version);
        LogState();
    }

    internal static void Scan()
    {
        var found = new List<DlItem>();
        try
        {

            var winPids = new HashSet<uint>();
            var winNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (p >= 100) return true;
                if (IsBrowser(h)) return true;
                string nm = Clean(t, m);
                if (!winNames.Add(nm)) return true;
                Win32.GetWindowThreadProcessId(h, out uint wp);
                winPids.Add(wp);
                found.Add(new DlItem("win:" + nm, nm, p, 0, 0, false, false, false, false, false, false, false,
                                     ExeOf(h), null, null, (int)wp, h));
                return true;
            }, IntPtr.Zero);

            var ph = StoreInstall.Poll(out string app, out int spct, out long done, out long total);
            if (ph != StoreInstall.Phase.None)
                found.Add(new DlItem("store:" + app, app, spct, done, total, false, false,
                                     ph == StoreInstall.Phase.Paused, ph == StoreInstall.Phase.Installing,
                                     ph == StoreInstall.Phase.Waiting, true, true, StoreAumid, null, null, 0, IntPtr.Zero));

            if (GameInstall.Poll(out string gApp, out long gDone, out long gTotal, out bool gStalled))
                found.Add(new DlItem("gdk:" + gApp, gApp, gTotal > 0 ? (int)Math.Clamp(gDone * 100 / gTotal, 0, 99) : 0,
                                     gDone, gTotal, gTotal <= 0, false, gStalled, false, false, true, false,
                                     StoreAumid, GameInstall.LogoPath, null, 0, IntPtr.Zero));

            if (SteamInstall.Current() is { } steam)
                found.Add(new DlItem("steam:" + steam.Name, steam.Name,
                                     (int)Math.Clamp(steam.Done * 100 / Math.Max(steam.Total, 1), 0, 99),
                                     steam.Done, steam.Total, false, false, false, false, false, false, false,
                                     SteamExe(), null, null, 0, IntPtr.Zero));

            foreach (var part in PartialFiles.All())
            {

                if (part.OwnerPid != 0 && winPids.Contains((uint)part.OwnerPid)) continue;
                long pTotal = BrowserDownloads.TotalFor(part.Path);

                string? learned = Downloaders.AppFor(System.IO.Path.GetDirectoryName(part.Path));
                if (learned != null && OwnerLooksLike(learned, part.OwnerPid)) learned = null;
                string label = part.Name.Length > 0 ? part.Name
                    : BrowserDownloads.NameFor(part.Path) ?? learned ?? "Downloading";

                bool noName = part.Name.Length == 0 && label == "Downloading";

                if (noName && ChromiumProgress.For(part.Path, part.Bytes) is { } live)
                {
                    found.Add(new DlItem("file:" + part.Path, live.Name,
                                         (int)Math.Clamp(live.Received * 100 / Math.Max(live.Total, 1), 0, 99),
                                         live.Received, live.Total, false, false, part.Stalled, false, false,
                                         false, false, part.OwnerPid != 0 ? ExeOfPid(part.OwnerPid) : null,
                                         null, part.Path, part.OwnerPid, IntPtr.Zero));
                    continue;
                }
                bool noPct = pTotal <= part.Bytes;
                found.Add(new DlItem("file:" + part.Path, label,
                                     noPct ? 0 : (int)Math.Clamp(part.Bytes * 100 / pTotal, 0, 99),
                                     part.Bytes, noPct ? 0 : pTotal, noPct, noName, part.Stalled, false, false,
                                     false, false, part.OwnerPid != 0 ? ExeOfPid(part.OwnerPid) : null,
                                     null, part.Path, part.OwnerPid, IntPtr.Zero));
            }
        }
        catch { }
        try { Publish(found); } catch { }
    }

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

    public static void CancelDownload()
    {
        bool browser = OwnerIsBrowser();
        CancelLog($"cancel clicked: name='{Name}' file='{FilePath}' browser={browser}");
        if (browser) { CancelInBrowser(); return; }
        StopOwner();
    }

    private static void CancelInBrowser()
    {
        var h = OwnerWindow();
        CancelLog($"cancelInBrowser owner={OwnerPid} exe='{ExePath}' hwnd={h}");
        if (h == IntPtr.Zero) { Reveal(); return; }
        string? target = null, partial = FilePath;
        try
        {

            if (Name is { Length: > 0 } shown && shown != "Downloading" && shown.Contains('.'))
                target = shown;
            if (target is null && partial is { Length: > 0 } fp
                && PartialFiles.IsPartial(fp, out string clean) && clean.Length > 0)
                target = clean;
        }
        catch { }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {

                bool focused = FocusAndConfirm(h);
                int rc = UiaCancel(h, target, focused);

                if (rc < 0 && focused) SendCtrlJ();
                bool stopped = StoppedGrowing(partial);
                CancelLog($"  uia rc={rc} target='{target}' focused={focused} stopped={stopped}");

                if (!stopped) Reveal();
            }
            catch (Exception ex) { CancelLog("  threw " + ex.Message); }
        });
    }

    private static bool StoppedGrowing(string? partial)
    {
        if (string.IsNullOrEmpty(partial)) return false;
        try
        {
            if (!System.IO.File.Exists(partial)) return true;
            long a = new System.IO.FileInfo(partial!).Length;
            Thread.Sleep(1500);
            if (!System.IO.File.Exists(partial)) return true;
            return new System.IO.FileInfo(partial!).Length == a;
        }
        catch { return true; }
    }

    private static int UiaCancel(IntPtr hwnd, string? target, bool canTab)
    {
        try
        {
            string script;
            using (var s = typeof(Downloads).Assembly.GetManifestResourceStream("Halo.Assets.uia-cancel.ps1"))
            {
                if (s == null) return -1;
                using var r = new System.IO.StreamReader(s);
                script = r.ReadToEnd();
            }
            script = script.Replace("__HWND__", ((long)hwnd).ToString())
                           .Replace("__CANTAB__", canTab ? "1" : "0")
                           .Replace("__TARGET__", (target ?? "").Replace("'", "''"));

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                 $"halo-uia-{Guid.NewGuid():N}.ps1");

            System.IO.File.WriteAllText(path, script, new UTF8Encoding(true));
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                                      @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                string outp = p.StandardOutput.ReadToEnd();

                if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return -1; }
                if (outp.Length > 0) CancelLog("  uia: " + outp.Replace("\r\n", " | ").Trim());
                return p.ExitCode;
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }
        catch { return -1; }
    }

    private static bool OwnerIsBrowser()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe)) return true;
        try
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(exe!).ToLowerInvariant();
            if (Array.IndexOf(Browsers, stem) >= 0) return true;
            return Process.GetProcessesByName(stem).Length >= 4;
        }
        catch { return true; }
    }

    public static void RevealOwner()
    {
        var h = OwnerWindow();
        if (h == IntPtr.Zero) { Reveal(); return; }
        Focus(h);
    }

    public static void OpenDownloadsList()
    {
        var h = OwnerWindow();
        CancelLog($"openList owner={OwnerPid} exe='{ExePath}' hwnd={h}");
        if (h == IntPtr.Zero) { Reveal(); return; }
        System.Threading.Tasks.Task.Run(() =>
        {
            try { if (FocusAndConfirm(h)) SendCtrlJ(); }
            catch (Exception ex) { CancelLog("  threw " + ex.Message); }
        });
    }

    private static bool FocusAndConfirm(IntPtr h)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Focus(h);
            for (int i = 0; i < 60 && Win32.GetForegroundWindow() != h; i++) Thread.Sleep(25);
            if (Win32.GetForegroundWindow() == h) { CancelLog($"  focused=True attempt={attempt + 1}"); return true; }
        }
        CancelLog("  focused=False");
        return false;
    }

    private static void SendCtrlJ()
    {
        const byte VkJ = 0x4A; const uint KeyUp = 2;
        Win32.keybd_event((byte)Win32.VK_CONTROL, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(VkJ, 0, 0, UIntPtr.Zero);
        Win32.keybd_event(VkJ, 0, KeyUp, UIntPtr.Zero);
        Win32.keybd_event((byte)Win32.VK_CONTROL, 0, KeyUp, UIntPtr.Zero);
    }

    internal static void CancelLog(string s)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "cancel-debug.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n");
        }
        catch { }
    }

    private static readonly string[] NeverKill =
        { "explorer", "svchost", "system", "dllhost", "searchhost", "runtimebroker", "halo.app", "halo" };

    private static void StopOwner()
    {
        int pid = OwnerPid; var path = FilePath;
        if (pid != 0 && pid != Environment.ProcessId)
        {
            string stem = "";
            try { stem = System.IO.Path.GetFileNameWithoutExtension(ExeOfPid(pid) ?? "").ToLowerInvariant(); } catch { }
            if (Array.IndexOf(NeverKill, stem) < 0)
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); p.WaitForExit(4000); }
                catch { }
        }
        if (!string.IsNullOrEmpty(path) && PartialFiles.IsPartial(path!, out _))
            try { System.IO.File.Delete(path!); } catch { }
        Name = null; FilePath = null; OwnerPid = 0; Percent = 0; Downloaded = Total = 0; NoPct = false;
        Interlocked.Increment(ref Version);
    }

    private static IntPtr OwnerWindow()
    {
        int pid = OwnerPid;
        string? exe = ExePath;
        IntPtr byPid = IntPtr.Zero, byExe = IntPtr.Zero;
        try
        {
            Win32.EnumWindows((h, _) =>
            {
                if (!Win32.IsWindowVisible(h) || Win32.GetWindowTextLengthW(h) < 1) return true;
                Win32.GetWindowThreadProcessId(h, out uint wp);
                if (pid != 0 && wp == (uint)pid) { byPid = h; return false; }
                if (byExe == IntPtr.Zero && exe != null
                    && string.Equals(ExeOfPid((int)wp), exe, StringComparison.OrdinalIgnoreCase)) byExe = h;
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return byPid != IntPtr.Zero ? byPid : byExe;
    }

    private static bool Focus(IntPtr h)
    {
        try
        {
            Win32.ShowWindow(h, Win32.SW_RESTORE);
            uint fore = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
            uint self = Win32.GetCurrentThreadId();
            bool attached = fore != 0 && fore != self && Win32.AttachThreadInput(fore, self, true);
            Win32.SetForegroundWindow(h);
            if (attached) Win32.AttachThreadInput(fore, self, false);
            return Win32.GetForegroundWindow() == h;
        }
        catch { return false; }
    }

    private const string StoreAumid = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";

    private static bool OwnerLooksLike(string name, int pid)
    {
        if (pid == 0) return false;
        try
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(ExeOfPid(pid) ?? "");
            return stem.Length > 0 && string.Equals(stem, name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

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

    private static string Clean(string title, Match m)
    {
        string s = title.Substring(m.Index + m.Length).TrimStart(']', ' ', '-', ':', '\t', '|', '»');
        return s.Length == 0 ? title.Trim() : s;
    }
}
