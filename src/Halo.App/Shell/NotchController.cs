using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Halo.ClaudeCode;
using Halo.Codex;
using Halo.Interop;
using Halo.Widgets;
using Windows.System;

namespace Halo.Shell;

internal enum NotchVisibilityAction
{
    None,
    Hide,
    ShowAndRender,
}

internal readonly record struct NotchVisibilityDecision(
    NotchVisibilityAction Action,
    bool ReturnEarly,
    bool HiddenForFullscreen);

internal static class NotchVisibility
{
    // the pill is always on screen (empty when no widget is active) — only fullscreen hides it
    internal static NotchVisibilityDecision Decide(bool fullscreen, bool hiddenForFullscreen)
    {
        if (fullscreen)
            return new(hiddenForFullscreen ? NotchVisibilityAction.None : NotchVisibilityAction.Hide,
                ReturnEarly: true, HiddenForFullscreen: true);

        return new(hiddenForFullscreen ? NotchVisibilityAction.ShowAndRender : NotchVisibilityAction.None,
            ReturnEarly: false, HiddenForFullscreen: false);
    }
}

internal sealed class AgentNoticeCoordinator
{
    private readonly Dictionary<int, AgentNotice> _previous = new();
    private readonly Dictionary<int, NoticeWindow> _pending = new();
    private long _nextOrder;
    private int _restore = -1;

    internal AgentNoticeCoordinator(int primary) => Primary = primary;

    internal int Primary { get; private set; }

    internal bool IsOpen(DateTimeOffset now) => _pending.Values.Any(window => window.Until >= now);

    internal void SetPrimary(int primary)
    {
        if (_restore < 0)
            Primary = primary;
    }

    internal void Observe(int widgetIndex, AgentNotice notice, DateTimeOffset now,
        bool desktopBacked = false, bool allowSelection = true)
    {
        _previous.TryGetValue(widgetIndex, out var previous);
        _previous[widgetIndex] = notice;

        bool started = notice.State == "working" && previous.State != "working";
        // only a fresh compactedAt counts — merely leaving "compacting" could be a cancelled compact
        bool compacted = notice.CompactedAt is { } doneAt && doneAt != previous.CompactedAt &&
            now - doneAt < TimeSpan.FromSeconds(30);
        // waiting_input no longer auto-opens the pill (user: no need for it to pop) — compact-done still does
        if (compacted)
            _pending[widgetIndex] = new NoticeWindow(now.AddSeconds(4), desktopBacked, _nextOrder++);

        if (started && allowSelection && _pending.Count == 0 && _restore < 0)
            Primary = widgetIndex;

        if (allowSelection)
            Select(now, static _ => true);
    }

    internal void Tick(DateTimeOffset now, Func<int, bool>? isActive = null, bool allowSelection = true)
    {
        foreach (var (index, window) in _pending.ToArray())
            if (window.Until < now)
                _pending.Remove(index);

        if (allowSelection)
            Select(now, isActive ?? (static _ => true));
    }

    private void Select(DateTimeOffset now, Func<int, bool> isActive)
    {
        if (_pending.Count > 0)
        {
            if (_restore < 0)
                _restore = Primary;

            Primary = _pending
                .OrderBy(pair => pair.Key == _restore ? 0 : pair.Value.DesktopBacked ? 1 : 2)
                .ThenBy(pair => pair.Value.Order)
                .First().Key;
            return;
        }

        if (_restore >= 0)
        {
            if (isActive(_restore))
                Primary = _restore;
            _restore = -1;
        }
    }

    private readonly record struct NoticeWindow(DateTimeOffset Until, bool DesktopBacked, long Order);
}

internal sealed class NotchController
{
    private const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    private const int ExpandedW = 560, ExpandedH = 220, ExpandedR = 30;
    private const int TintDeskCollapsed = 255, TintDeskExpanded = 245;
    private const int TintAppCollapsed = 120, TintAppExpanded = 60;
    private const float OpenSeconds = 0.30f, CloseSeconds = 0.38f; // open snappier than close. slowed after
    // the _dt fix made these hit their real wall-clock duration (old timer cadence ran them ~2x slower).
    private const float HoldSeconds = 0.75f; // press-and-hold on the pill this long → drag-to-move engages
    private const int CaptureFast = 2, CaptureSlow = 12; // glass capture cadence: ~60fps expanded, ~10fps collapsed
    private const int EmptyCatchAlpha = 1; // empty pill fades to this alpha: invisible, but ≥1 so it still catches OLE file drags

    private readonly LayeredNotch _notch;
    private readonly StatusStore _claudeStore;
    private readonly CodexStatusStore _codexStore;
    private readonly CodexDesktopRuntime _codexDesktopRuntime;
    private readonly IWidget[] _widgets;
    private readonly MediaSessions _mediaSessions; // shared: maps live players to slots (one widget each)
    private readonly AgentNoticeCoordinator _agentNotices;
    private readonly DispatcherQueueTimer _timer;

    // geometry derived per-use: work area and Scale both change at runtime
    private float S => _notch.Scale;
    private int Sc(int v) => (int)MathF.Round(v * S);
    private int _cl => _notch.WorkLeft + (_notch.WorkWidth - Sc(CollapsedW)) / 2 + (int)_offsetX;
    private int _el => _notch.WorkLeft + (_notch.WorkWidth - Sc(ExpandedW)) / 2 + (int)_offsetX;
    private int _ct => _notch.WorkTop;
    private int _et => _notch.WorkTop;

    private int _primary;
    private int _userPicked = -1; // widget the user explicitly clicked into the pill — never auto-hidden
    private float _progress;
    private float _menu;        // circle → dropdown open, 0..1
    private float _drop = -1f;  // <0 idle, else 0..1 "drop into pill" animation
    private float _arrive = -1f; // <0 idle, else 0..1 new-app "opening" bloom after a swap
    private int _pending;
    private float _dropCX, _dropCY; // clicked/target circle centre, relative to the strip's top-left
    private bool _dropOut;      // drop runs pill → circle (new-app arrival toss)
    private string _dropIcon = "";
    private Bitmap? _dropImage;
    private readonly bool[] _prevActive;
    private int _row = -1;      // app row whose session fan is opening
    private float _rowOpen;
    private float _stripT;      // 0..1 — the swap-circle strip eases in/out instead of popping
    private int _widgetVersion = -1;
    private int _lastSec = -1;
    private bool _lastMouseDown;
    private bool _prevDragActive;   // edge-detect the end of a file drag
    private long _trayShowUntil;    // keep the tray primary this long after a drop so the file is seen land
    // File Tray row interaction: click opens · Ctrl+click selects · drag up/down reorders · drag out extracts
    private string? _trayPressPath;
    private Win32.POINT _trayPressAt;
    private int _trayMode = -1; // -1 idle, 0 pending, 1 reordering, 2 dragging out
    private bool _lastTrayDown;
    private bool _resizing;
    private Win32.POINT _resizeFrom;
    private float _scale0, _handle;
    private bool _hiddenForFullscreen;
    // drag-to-move: press-and-hold ~3s on the pill → it collapses and follows the cursor; release drops
    // it, and it snaps back to centre when parked near the default spot (magnet). _offsetX is persisted.
    private float _offsetX;
    private bool _moving;
    private float _holdT;   // 0..1 fill of the press-hold before move engages (drawn as a growing underline)
    private DateTime _holdStart = DateTime.MaxValue;
    private Win32.POINT _holdAnchor;
    private int _moveGrabDX; // cursor-X − pill-centre at grab, so the pill doesn't jump on pickup
    private bool _pinned;   // pin button: keep the pill on top of everything, even fullscreen apps
    private float _pinHov;  // eased 0..1 hover brightness, so the glyph breathes instead of snapping
    private float _shrink;  // no-app tuck-away: 0 = normal pill, 1 = invisible alpha≈1 drop-catch strip
    private bool _empty; // no active widgets: pill stays visible but renders blank

    // notification banner: the pill itself morphs into a mirrored toast (Windows' own banner is
    // yanked by NotifSource). No close button — clicking anywhere outside dismisses it.
    private readonly Halo.Notifications.NotifSource _notifSrc = new();
    private Halo.Notifications.BtBattery? _bt; // keeps the connect-watcher (and its timer) alive
    private readonly Widgets.BtWidget _btWidget = new(); // transient collapsed-pill battery display on connect
    private System.Threading.Timer? _testTrigger; // demo: file-driven fake banners for recordings (see PollTestNotif)
    private Halo.Notifications.NotifItem? _notif;
    private float _notifT;        // 0..1 pill → banner morph
    private bool _notifClosing;
    private bool _notifDetailOn;  // grabber clicked → full message
    private float _notifDetail;   // 0..1 summary → detail height
    private int _notifDetailH = NotifBanner.SummaryH + 60;
    private DateTime _notifDeadline;
    private int _curW = CollapsedW, _curH = CollapsedH; // last applied logical dims (banner hit-tests)
    private bool _lastDesktop = true;
    private IntPtr _lastFg = IntPtr.Zero;
    private uint _lastLangId;   // last foreground keyboard LANGID — detect Alt+Shift language switches
    private IntPtr _langFg;     // window that layout belonged to (only notify on a same-window switch)
    private long _langFgSince;  // when _langFg became foreground — swallow the OS's lazy per-app layout apply
    private IntPtr _behind = IntPtr.Zero;
    private int _captureTick;
    private int _animTick;
    private int _lastCaptureVer;
    // edge-triggered system alerts (throttled to ~1/s in CheckAlerts); each flag fires once per episode
    private long _alertAt;
    private bool _battWarned;                                   // low-battery banner already shown this discharge
    // usage>=80% banners: one per RESET WINDOW, keyed by the window's reset time — a value that dips
    // and climbs again (Codex rollout values oscillate between sessions) must NOT re-fire ("اسپم").
    // Persisted: an in-memory-only map re-fired the banner after every restart/deploy.
    private readonly Dictionary<string, (DateTimeOffset reset, DateTime at)> _limitFired = LoadLimitFired();
    private static readonly string LimitFiredPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "limit-fired.txt");

    // plain lines "key|resetIso|firedIso" — no JSON dep needed
    private static Dictionary<string, (DateTimeOffset, DateTime)> LoadLimitFired()
    {
        var d = new Dictionary<string, (DateTimeOffset, DateTime)>();
        try
        {
            foreach (var line in System.IO.File.ReadAllLines(LimitFiredPath))
            {
                var p = line.Split('|');
                if (p.Length == 3 && DateTimeOffset.TryParse(p[1], out var r) && DateTime.TryParse(p[2], null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var a))
                    d[p[0]] = (r, a);
            }
        }
        catch { }
        return d;
    }

    private void SaveLimitFired()
    {
        try
        {
            var lines = new List<string>();
            foreach (var (k, v) in _limitFired) lines.Add($"{k}|{v.reset:o}|{v.at:o}");
            System.IO.File.WriteAllLines(LimitFiredPath, lines);
        }
        catch { }
    }
    private bool _netBadShown;                                  // "bad internet" already shown this bad spell
    public NotchController(LayeredNotch notch)
    {
        _notch = notch;
        _notch.ClipboardImage += OnClipboardImage; // Windows won't deliver the snip toast → mirror it from the clipboard
        _claudeStore = new StatusStore();
        _codexStore = new CodexStatusStore();
        _codexDesktopRuntime = CodexDesktopRuntime.Shared;
        CodexLimits.Attach(_codexStore);
        CodexLimits.UpdateFrom(_codexStore.Current);
        _mediaSessions = new MediaSessions();
        var widgets = new List<IWidget>();
        for (int s = 0; s < MediaSessions.MaxSlots; s++)
            widgets.Add(new MediaWidget(_mediaSessions, s)); // one widget per live player (Spotify + browser = two circles)
        widgets.Add(new VlcWidget(_mediaSessions)); // classic VLC has no SMTC — window-title + hotkey path
        widgets.Add(new DownloadWidget()); // best-effort download progress (window-title % scan)
        widgets.Add(new FileTray());       // drag-a-file-over-the-pill shelf (OLE drop target in LayeredNotch)
        widgets.Add(_btWidget);            // transient BT battery display (collapsed-pill takeover on connect)
        Privacy.Poke(); // start the mic/camera-in-use watcher (drives the privacy dot)
        for (int s = 0; s < StatusStore.MaxSessions; s++)
        {
            int slot = s; // one widget per CC session slot; cancel targets that session's pid
            widgets.Add(new ClaudeCodeWidget(_claudeStore, slot, () => CancelClaude(slot)));
        }
        widgets.Add(new CodexWidget(_codexStore, CodexSurface.Desktop, () => CancelCodex(CodexSurface.Desktop),
            () => _codexDesktopRuntime.Presence.Running));
        widgets.Add(new CodexWidget(_codexStore, CodexSurface.Cli, () => CancelCodex(CodexSurface.Cli)));
        var agentStore = GenericAgentWidget.NewStore(); // any other AI tool: ~/.halo/agents/agent-*.json
        for (int s = 0; s < StatusStore.MaxSessions; s++)
            widgets.Add(new GenericAgentWidget(agentStore, s));
        _widgets = [.. widgets];

        var active = ActiveIndices();
        LoadOffset(); // restore where the user last parked the pill
        _notch.SetCapturable(_pinned); // pinned pill shows up in screenshots/recordings; unpinned stays hidden
        _empty = active.Length == 0;
        _shrink = _empty ? 1f : 0f; // boot straight into the right size, no opening animation
        if (!_empty) _primary = active[0];
        _prevActive = new bool[_widgets.Length];
        for (int i = 0; i < _widgets.Length; i++) _prevActive[i] = _widgets[i].IsActive;
        Apply(0f); // empty or not, the pill shows from the first frame (boot = blank pill)
        _agentNotices = new AgentNoticeCoordinator(_primary);

        // headphones/AirPods/phone connect → the collapsed pill briefly shows the device + battery ring
        _bt = new Halo.Notifications.BtBattery((name, pct) => _btWidget.Show(name, pct));
        _testTrigger = new System.Threading.Timer(_ => PollTestNotif(), null, 1000, 1000);

        Dispatcher.Ensure();
        var dq = DispatcherQueue.GetForCurrentThread();
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(8);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        // one bad frame must never kill the pill — media-session races (two players), transient WinRT/GDI
        // hiccups. skip it and log; the app has hard-crashed here before on unhandled render exceptions.
        try { Frame(); } catch (Exception ex) { CrashLog(ex); }
    }

    private static void CrashLog(Exception ex)
    {
        try
        {
            var p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "frame-errors.txt");
            System.IO.File.WriteAllText(p, $"{DateTime.Now:HH:mm:ss}\n{ex}"); // latest only — no unbounded growth
        }
        catch { }
    }

    // adaptive cadence: 120fps when the CPU has headroom, 60fps when it's busy (saves heat/battery).
    // "if resources are free → 120" per the user; sampled ~1s from GetSystemTimes (cheap, no counters).
    private long _cpuIdle, _cpuBusyBase, _cpuAt;
    private int _fps = 120;
    // "heavy" = something else is hammering the CPU. While heavy we back Halo off (lower fps, slower
    // glass capture) AND drop our process priority so the busy app gets the cores — then fire ONE notice.
    private bool _heavy;
    // usage notices: escalating one-shot tiers (per the user). CPU fires at 50/70/85/95%, RAM at
    // 70/85/95% — each tier at most ONCE per session; after a tier fires only a HIGHER tier can fire.
    // The ladder resets when Halo restarts (logon → effectively per boot). 10 consecutive 1s samples
    // above the tier are required so launch spikes don't count.
    private static readonly int[] CpuTiers = { 50, 70, 85, 95 };
    private static readonly int[] RamTiers = { 70, 85, 95 };
    private int _cpuTierFired = -1, _ramTierFired = -1; // index of highest tier already notified
    private int _cpuStreak, _ramStreak;
    internal bool Heavy => _heavy; // Frame() slows glass capture while heavy
    private void AdaptFrameRate()
    {
        long now = Environment.TickCount64;
        if (now - _cpuAt < 1000) return;
        _cpuAt = now;
        if (!Win32.GetSystemTimes(out long idle, out long kern, out long user)) return;
        long total = kern + user; // kernel time already includes idle
        // while the panel is open (or a banner is up) the user is watching → smoothness beats battery,
        // so never throttle then. The whole point of backing off is when nothing's on screen.
        bool watching = _progress > 0.02f || _notif != null || _drop >= 0f;
        int target = _fps;
        if (_cpuBusyBase != 0 && total > _cpuBusyBase)
        {
            float busy = 1f - (float)(idle - _cpuIdle) / (total - _cpuBusyBase);
            // watching (panel open) → hold a solid 60. The open panel's glass blur is ~4x the collapsed
            // cost (measured ~58% CPU at 120fps vs ~15% collapsed), so chasing 120 leaves no headroom and
            // stutters the moment a download loads the machine. 60 halves Halo's load and stays smooth.
            // Collapsed: 60 is the floor for normal load; only truly slammed drops to 30.
            if (watching) target = 60;
            else if (busy > 0.90f) target = 30;       // only truly slammed drops to 30
            else if (busy > 0.55f) target = 60;       // busy: hold a smooth 60
            else if (busy < 0.45f) target = 120;      // headroom: full 120 (hysteresis band between)

            // "heavy" backs off glass capture AND drops our priority — but NOT while watching, or the
            // panel itself stutters (lower priority = our render thread gets preempted mid-frame).
            bool heavy = !watching && (_heavy ? busy > 0.40f : busy > 0.50f); // enter 50%, leave 40%
            if (heavy != _heavy)
            {
                _heavy = heavy;
                try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                    heavy ? System.Diagnostics.ProcessPriorityClass.BelowNormal
                          : System.Diagnostics.ProcessPriorityClass.Normal; } catch { }
            }
            int pctNow = (int)(busy * 100);
            int tier = TierOf(CpuTiers, pctNow);
            _cpuStreak = tier > _cpuTierFired ? _cpuStreak + 1 : 0;
            if (_cpuStreak >= 10) { _cpuTierFired = tier; _cpuStreak = 0; QueueCpuNotice(pctNow); }
            CheckRam();
        }
        _cpuIdle = idle; _cpuBusyBase = total;
        if (target != _fps)
        {
            _fps = target;
            _timer.Interval = TimeSpan.FromMilliseconds(target >= 120 ? 8 : target >= 60 ? 16 : 33);
        }
    }

    // highest tier index the value reaches, or -1 below the first tier
    private static int TierOf(int[] tiers, int pct)
    {
        int t = -1;
        for (int i = 0; i < tiers.Length; i++) if (pct >= tiers[i]) t = i;
        return t;
    }

    // RAM ladder, sampled on the same 1s cadence (GlobalMemoryStatusEx is a single cheap syscall)
    private void CheckRam()
    {
        var ms = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref ms)) return;
        int pct = (int)ms.dwMemoryLoad;
        int tier = TierOf(RamTiers, pct);
        _ramStreak = tier > _ramTierFired ? _ramStreak + 1 : 0;
        if (_ramStreak >= 10) { _ramTierFired = tier; _ramStreak = 0; QueueRamNotice(pct); }
    }

    private void QueueRamNotice(int pct)
        => QueueLoadNotice("memory", pct, TopRamProcess, "Memory is running low.");

    // The RAM and CPU notices were two copies of the same banner, which is how the pt-BR pull request
    // ended up translating one and missing the other. One shape now: the resource word, whose process
    // list to blame, and whether an unknown top process still deserves a banner (CPU: no, RAM: yes).
    // Sampling CPU takes ~500ms → stays off the UI thread; EnqueueLocal is thread-safe. English (Halo rule).
    private void QueueLoadNotice(string resource, int pct, Func<string?> topProcess, string? fallbackBody)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            string? top = topProcess();
            string? body = top != null ? $"{top} is using the most." : fallbackBody;
            if (body == null) return;
            _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
            {
                App = "System", Title = $"High {resource} usage — {pct}%",
                Body = body, Kind = "cpu", Duration = 7, Icon = CpuBadge(),
            });
        });
    }

    // heaviest process by working set (instant, no sampling window needed)
    private static string? TopRamProcess()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcesses();
            string? best = null; long bestWs = 0; int self = Environment.ProcessId;
            foreach (var p in procs)
            {
                try { if (p.Id != self && p.Id > 4 && p.WorkingSet64 > bestWs) { bestWs = p.WorkingSet64; best = p.ProcessName; } }
                catch { }
            }
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
            return best == null || best.Length == 0 ? null : char.ToUpperInvariant(best[0]) + best[1..];
        }
        catch { return null; }
    }

    // once per tier: name the process eating the most CPU. Sampling two TotalProcessorTime
    // snapshots takes ~500ms → do it off the UI thread; EnqueueLocal is thread-safe. English (Halo rule).
    private void QueueCpuNotice(int sysPct)
        => QueueLoadNotice("CPU", sysPct, TopCpuProcess, null);

    // heaviest process by CPU-time delta over a short window (self + idle/system excluded).
    private static string? TopCpuProcess()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcesses();
            var t0 = new Dictionary<int, TimeSpan>();
            foreach (var p in procs) { try { t0[p.Id] = p.TotalProcessorTime; } catch { } }
            System.Threading.Thread.Sleep(450);
            string? best = null; double bestMs = 0; int self = Environment.ProcessId;
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4 || !t0.TryGetValue(p.Id, out var a)) continue;
                    p.Refresh();
                    double ms = (p.TotalProcessorTime - a).TotalMilliseconds;
                    if (ms > bestMs) { bestMs = ms; best = p.ProcessName; }
                }
                catch { }
            }
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
            return best == null || best.Length == 0 ? null : char.ToUpperInvariant(best[0]) + best[1..];
        }
        catch { return null; }
    }

    // System alerts, throttled to ~1/s and edge-triggered so we don't nag: battery <=20% (click → Power
    // Saver), Claude/Codex usage >=80%, slow internet. Each flag re-arms once the condition clears.
    private void CheckAlerts()
    {
        long now = Environment.TickCount64;
        if (now - _alertAt < 1000) return;
        _alertAt = now;
        if (_pinned) _notch.AssertTopmost(); // keep a pinned pill above fullscreen apps (survives reboot/autostart)
        CheckBattery();
        CheckLimit("Claude", ClaudeCode.Limits.FiveHour, ClaudeCode.Limits.FiveHourReset, "5-hour");
        CheckLimit("Claude", ClaudeCode.Limits.Week, ClaudeCode.Limits.WeekReset, "weekly");
        CheckLimit("Codex", CodexLimits.FiveHour, CodexLimits.FiveHourReset, "primary");
        CheckLimit("Codex", CodexLimits.Week, CodexLimits.WeekReset, "weekly");
        CheckInternet();
        CheckHourly();
    }

    // on the hour (2:00, 3:00 …) a small glance banner with the time. Init to the current hour so
    // launching mid-hour doesn't fire, and starting exactly at :00 chimes only once (hour-change guard).
    private int _chimedHour = DateTime.Now.Hour;
    private void CheckHourly()
    {
        var t = DateTime.Now;
        if (t.Minute != 0 || t.Hour == _chimedHour) return;
        _chimedHour = t.Hour;
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = "Clock", Title = t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
            Kind = "hourly", Duration = 4, Icon = ClockBadge(),
        });
    }

    // demo/test hook: write "<type>[|<arg>[|<proc>]]" into %LOCALAPPDATA%\Halo\notif-test.txt to fire a
    // fake local banner on command for recordings (real CPU/RAM/hourly triggers are hard to time). Reuses
    // the exact same banner shapes as the real alerts — the real alert logic is untouched.
    //   cpu|92   ·   ram|88   ·   clock|2
    private static readonly string TestNotifPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "notif-test.txt");
    private void PollTestNotif()
    {
        try
        {
            if (!System.IO.File.Exists(TestNotifPath)) return;
            var line = System.IO.File.ReadAllText(TestNotifPath).Trim();
            System.IO.File.Delete(TestNotifPath);
            if (line.Length == 0) return;
            var parts = line.Split('|');
            string type = parts[0].Trim().ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";
            string proc = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : "";
            switch (type)
            {
                // the demo banners go through the same builder as the real ones, so a wording or
                // translation change can't apply to one and miss the other
                case "cpu": case "sys": case "system":
                    QueueLoadNotice("CPU", int.TryParse(arg, out var cp) ? cp : 92,
                        () => proc.Length > 0 ? proc : TopCpuProcess() ?? "Chrome", null);
                    break;
                case "ram": case "mem": case "memory":
                    QueueLoadNotice("memory", int.TryParse(arg, out var rp) ? rp : 88,
                        () => proc.Length > 0 ? proc : TopRamProcess() ?? "Chrome", null);
                    break;
                case "clock": case "hour": case "hourly":
                    var t = int.TryParse(arg, out var hr) && hr is >= 0 and <= 23 ? DateTime.Today.AddHours(hr) : DateTime.Now;
                    _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
                    {
                        App = "Clock", Title = t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
                        Kind = "hourly", Duration = 5, Icon = ClockBadge(),
                    });
                    break;
            }
        }
        catch { }
    }

    private void CheckBattery()
    {
        if (!Win32.GetSystemPowerStatus(out var s)) return;
        bool onBattery = s.ACLineStatus == 0;   // 1 = plugged, 255 = unknown
        int pct = s.BatteryLifePercent;          // 255 = unknown
        if (!onBattery) { _battWarned = false; return; }   // plugged → re-arm
        if (pct > 20 || pct > 100) { _battWarned = false; return; } // above threshold / unknown → re-arm
        if (_battWarned) return;
        _battWarned = true;
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = "Battery", Title = $"Battery low — {pct}%", Body = "Tap to turn on Power Saver.",
            Kind = "battery", Duration = 8, OnActivate = EnablePowerSaver, Icon = BatteryBadge(),
        });
    }

    // switch to the built-in Power saver scheme (GUID). ponytail: some Win11 builds hide legacy plans,
    // in which case this is a no-op — the honest best-effort of an "enable power saver like Apple" tap.
    private static void EnablePowerSaver()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powercfg", Arguments = "/setactive a1841308-3541-4fab-bc81-f71556f20b4a",
                UseShellExecute = false, CreateNoWindow = true,
            });
        }
        catch { }
    }

    // one banner per reset window. Codex recomputes ResetsAt from rollout events, so it DRIFTS by
    // seconds between reads — exact equality re-fired after every restart. Same window = reset within
    // 30min of the recorded one; plus a hard 6h cooldown per key regardless.
    private void CheckLimit(string app, float util, DateTimeOffset reset, string window)
    {
        if (util < 0.80f) return;                   // below threshold (or unknown)
        string key = app + window;
        if (_limitFired.TryGetValue(key, out var f)
            && (DateTime.UtcNow - f.at < TimeSpan.FromHours(6)
                || (reset != default && f.reset != default && (reset - f.reset).Duration() < TimeSpan.FromMinutes(30))))
            return;
        _limitFired[key] = (reset, DateTime.UtcNow);
        SaveLimitFired(); // survive restarts — a deploy must not re-banner the same window
        int p = (int)(util * 100);
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = app, Title = $"{app} usage {p}%", Body = $"You've used {p}% of your {window} limit.",
            Kind = $"limit-{app}-{window}", Duration = 8, Icon = LimitBadge(),
        });
    }

    private void CheckInternet()
    {
        if (!ClaudeCode.NetMon.Slow) { _netBadShown = false; return; }
        if (_netBadShown) return;
        _netBadShown = true;
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = "Network", Title = "Bad internet :/", Kind = "net", Duration = 6, Icon = NetBadge(),
        });
    }

    // real per-frame delta (seconds), so animations run at the same wall-clock speed whether the
    // adaptive timer is at 120 or 60fps. clamped so a stall/resume advances one step, not a leap.
    private float _dt = 0.008f;
    private long _lastFrameAt;
    private void Frame()
    {
        long frameNow = Environment.TickCount64;
        _dt = _lastFrameAt == 0 ? 0.008f : Math.Clamp((frameNow - _lastFrameAt) / 1000f, 0.001f, 0.05f);
        _lastFrameAt = frameNow;
        AdaptFrameRate();
        CheckAlerts();
        var notifStart = _notif; // an in-place banner swap (rapid language flip) must force a redraw
        var fg = Win32.GetForegroundWindow();
        DetectCompactCancel(fg);
        DetectLanguageChange(fg);
        bool fullscreen = !_pinned && _notch.IsFullscreen(fg); // pinned: stay over games/movies
        var active = fullscreen ? [] : ActiveIndices();
        // a live/queued toast overrides the fullscreen hide: the pill stays empty (active = [])
        // but the banner still wakes and renders over the game
        bool notifLive = _notif != null || _notifSrc.HasPending;
        var visibility = NotchVisibility.Decide(fullscreen && !notifLive, _hiddenForFullscreen);
        _hiddenForFullscreen = visibility.HiddenForFullscreen;

        if (visibility.Action == NotchVisibilityAction.Hide)
            _notch.SetVisible(false);
        else if (visibility.Action == NotchVisibilityAction.ShowAndRender)
        {
            if (active.Length > 0 && Array.IndexOf(active, _primary) < 0)
            {
                _primary = active[0];
                _agentNotices.SetPrimary(_primary);
            }
            _notch.SetVisible(true); // empty no longer hides the window (it's an invisible drop-catcher) — only fullscreen did
            _lastFg = IntPtr.Zero;
            Apply(_progress);
        }

        if (visibility.ReturnEarly)
            return;

        bool wasEmpty = _empty;
        _empty = active.Length == 0;
        // primary must be an active widget; fall back to the first active one if it went inactive
        if (!_empty && _drop < 0f && Array.IndexOf(active, _primary) < 0)
        {
            _primary = active[0];
            _agentNotices.SetPrimary(_primary);
        }

        // Any agent can request a temporary expansion. Keep an active dropdown intact, then surface
        // the queued notice once its drop animation finishes.
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _widgets.Length; i++)
        {
            bool desktopBacked = _widgets[i] is CodexWidget codex && codex.IsDesktop;
            _agentNotices.Observe(i, _widgets[i].AgentNotice, now, desktopBacked, allowSelection: _drop < 0f);
        }
        _agentNotices.Tick(now, i => _widgets[i].IsActive, allowSelection: _drop < 0f);
        if (_drop < 0f)
            _primary = _agentNotices.Primary;
        if (!_empty && Array.IndexOf(active, _primary) < 0)
        {
            _primary = active[0];
            _agentNotices.SetPrimary(_primary);
        }

        if (_userPicked >= 0 && Array.IndexOf(active, _userPicked) < 0) _userPicked = -1; // picked app closed → forget it

        // user rule: a Claude session that's actively coding outranks passive widgets (media) for the
        // pill — if it settled on a non-working widget but a working Claude is active, prefer Claude.
        // Manual pick wins; Claude-hide below still applies when you're focused IN that Claude terminal.
        if (_drop < 0f && !_empty && _userPicked < 0 && _widgets[_primary].AgentNotice.State != "working")
            foreach (var i in active)
                if (_widgets[i] is ClaudeCodeWidget && _widgets[i].AgentNotice.State == "working")
                {
                    _primary = i;
                    _agentNotices.SetPrimary(i);
                    break;
                }

        // user rule: when the Claude session's OWN terminal is focused, don't mirror it in the pill —
        // you're already looking at it. Surface another active widget instead; keep it only if it's the
        // only one, OR you explicitly clicked it in. Detection = the foreground window is an ancestor of
        // the Claude process.
        if (_drop < 0f && !_empty && active.Length > 1 && _primary != _userPicked
            && _widgets[_primary] is ClaudeCodeWidget)
        {
            Win32.GetWindowThreadProcessId(fg, out uint fpid);
            if (fpid != 0 && FgHostsWidget((int)fpid, _primary))
                foreach (var i in active)
                    if (i != _primary && !FgHostsWidget((int)fpid, i))
                    {
                        _primary = i;
                        _agentNotices.SetPrimary(i);
                        break;
                    }
        }
        bool notice = _drop < 0f && _agentNotices.IsOpen(now);

        // user rule: an active download outranks everything else for the pill ("از بقیه اولویت‌ها ببر جلو").
        // Still swappable — an explicit pick wins, and a live compact-done notice keeps its 4s slot.
        if (_drop < 0f && !_empty && _userPicked < 0 && !notice)
            for (int i = 0; i < _widgets.Length; i++)
                if (_widgets[i] is DownloadWidget && _widgets[i].IsActive)
                { _primary = i; _agentNotices.SetPrimary(i); break; }

        // a just-connected BT device briefly owns the collapsed pill (device + battery ring), then releases
        if (_drop < 0f && _btWidget.IsActive)
            for (int i = 0; i < _widgets.Length; i++)
                if (_widgets[i] is BtWidget) { _primary = i; _agentNotices.SetPrimary(i); break; }

        // a live file drag grabs the pill instantly so the drop zone is right there (reveal-on-drag),
        // and the tray keeps the pill for ~2.5s AFTER the drop so the dropped file is actually seen land —
        // otherwise an active download (which "outranks everything", above) snatches the pill back the
        // instant DragActive clears and the tray never shows ("همون دانلودر باز میشه بجای File Tray").
        if (_prevDragActive && !FileTray.DragActive) _trayShowUntil = Environment.TickCount64 + 2500;
        _prevDragActive = FileTray.DragActive;
        if (_drop < 0f && (FileTray.DragActive || Environment.TickCount64 < _trayShowUntil))
            for (int i = 0; i < _widgets.Length; i++)
                if (_widgets[i] is FileTray)
                { _primary = i; _agentNotices.SetPrimary(i); break; }

        // a supported app just appeared → toss its icon out of the pill into the swap circle
        for (int i = 0; i < _widgets.Length; i++)
        {
            bool isAct = _widgets[i].IsActive;
            if (isAct && !_prevActive[i] && !fullscreen && _drop < 0f)
            {
                if (i == _primary) _arrive = 0f; // became the pill itself → bloom
                else if (_progress < 0.1f)
                {
                    _pending = _primary; // arrival: primary stays put
                    _dropOut = true;
                    _dropIcon = _widgets[i].Icon;
                    _dropImage = _widgets[i].IconImage;
                    _dropCX = _dropCY = LayeredNotch.CircleD / 2f; // strip is closed — land on the circle
                    _drop = 0f;
                }
            }
            _prevActive[i] = isAct;
        }

        Win32.GetCursorPos(out var p);

        // ── notification banner: mirror the next toast once the pill is idle ──
        if (_notif == null && !_notifClosing && _progress <= 0.02f && _drop < 0f
            && _notifSrc.Dequeue() is { } item)
        {
            _notif = item;
            _notifDetailOn = false;
            _notifDetail = 0f;
            _notifDetailH = NotifBanner.DetailHeight(item);
            _notifDeadline = DateTime.UtcNow.AddSeconds(item.Duration); // 6s for real toasts; 1s for language flips
        }
        float prevNotifT = _notifT, prevNotifDetail = _notifDetail;
        bool overNotif = false;
        if (_notif != null)
        {
            overNotif = InRect(p, NotifLeft(), _ct, Sc(_curW), Sc(_curH));
            if (overNotif && !_notifDetailOn && _notif.Kind != "language") // reading it? don't yank it away mid-glance
                _notifDeadline = Max(_notifDeadline, DateTime.UtcNow.AddSeconds(2.5)); // (language flips are a hard 1s)
            if (!_notifDetailOn && DateTime.UtcNow > _notifDeadline) _notifClosing = true;
            _notifT = Math.Clamp(_notifT + (_notifClosing ? -_dt / 0.30f : _dt / 0.24f), 0f, 1f);
            _notifDetail = Math.Clamp(_notifDetail + (_notifDetailOn ? 1 : -1) * _dt / 0.22f, 0f, 1f);
            if (_notifClosing && _notifT <= 0f)
            {
                _notif = null;
                _notifClosing = false;
                _notifDetailOn = false;
                _notifDetail = 0f;
            }
        }

        // corner-drag resize: grab the handle zone, drag = live global rescale, release = persist
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        bool inHandle = _progress > 0.9f
            && p.X >= _el + Sc(ExpandedW - 44) && p.X < _el + Sc(ExpandedW) + 8
            && p.Y >= _et + Sc(ExpandedH - 44) && p.Y < _et + Sc(ExpandedH) + 8;
        bool rescaled = false;
        if (_resizing)
        {
            if (down)
            {
                float ns = Math.Clamp(_scale0
                    + ((p.X - _resizeFrom.X) + (p.Y - _resizeFrom.Y)) / (float)(ExpandedW + ExpandedH),
                    0.7f, 1.6f);
                rescaled = ns != _notch.Scale;
                _notch.Scale = ns;
            }
            else { _resizing = false; _notch.SaveScale(); }
        }
        else if (down && !_lastMouseDown && inHandle && !_moving)
        {
            _resizing = true;
            _resizeFrom = p;
            _scale0 = _notch.Scale;
        }
        float prevHandle = _handle;
        _handle = Math.Clamp(_handle + (inHandle || _resizing ? 1 : -1) * _dt / 0.12f, 0f, 1f);
        _notch.HandleAlpha = _handle;

        bool hovered = _resizing || _moving || (_progress > 0.02f
            ? InRect(p, _el, _et, Sc(ExpandedW), Sc(ExpandedH))
            : InRect(p, _cl, _ct, Sc(CollapsedW), Sc(CollapsedH)));
        float prevOffsetX = _offsetX, prevHoldT = _holdT;
        UpdateMove(p, down, hovered);
        // an empty pill has nothing to expand into; a live banner owns the pill until it's gone;
        // while dragging, the pill stays collapsed so it's a small puck to move
        bool open = (hovered || notice || FileTray.DragActive) && !_empty && _notif == null && !_moving;

        int dir = open ? 1 : -1;
        float step = _dt / (open ? OpenSeconds : CloseSeconds);
        // a live file drag snaps to full size INSTANTLY (no glide): a half-grown window lets the cursor
        // fall past the drop-zone the user sees onto the app behind, which then opens the file instead of
        // the tray catching it ("یه صفحه دیگه باز میشه"). The reliable drop target must be full-size at once.
        float next = open && FileTray.DragActive ? 1f : Math.Clamp(_progress + dir * step, 0f, 1f);

        // strip: apps open downward while hovering; the hovered row's sessions fan out rightward
        int alt = AltIndices().Length;
        bool inMenu = _progress < 0.05f && _drop < 0f && InMenu(p);
        float mnext = alt >= 2 && inMenu ? Math.Min(_menu + step, 1f) : Math.Max(_menu - step, 0f);

        var rows = Groups();
        int hoverRow = -1;
        if (inMenu && p.Y >= _ct)
        {
            int r0 = (p.Y - _ct) / Sc(LayeredNotch.CircleD);
            if (r0 >= 0 && r0 < rows.Count) hoverRow = r0;
        }
        if (hoverRow != _row && hoverRow >= 0) { _row = hoverRow; _rowOpen = 0f; }
        float rnext = _row >= 0 && _row < rows.Count && rows[_row].Length >= 2 && inMenu && hoverRow == _row
            ? Math.Min(_rowOpen + step, 1f)
            : Math.Max(_rowOpen - step, 0f);
        if (mnext <= 0f && rnext <= 0f) _row = -1;

        // drop-into-pill animation; on landing, kick off the "opening" bloom for the new app
        float dnext = _drop;
        if (_drop >= 0f)
        {
            dnext = _drop + _dt / 0.34f; // slower = more liquid
            if (dnext >= 1f)
            {
                if (!_dropOut) { _primary = _pending; _agentNotices.SetPrimary(_primary); _arrive = 0f; _userPicked = _pending; }
                _dropOut = false;
                dnext = -1f;
            }
        }

        float anext = _arrive;
        if (_arrive >= 0f) { anext = _arrive + _dt / 0.22f; if (anext >= 1f) anext = -1f; }

        // commit menu/drop before PollClick so a click that starts a drop isn't clobbered
        float prevMenu = _menu, prevDrop = _drop, prevArrive = _arrive, prevRowOpen = _rowOpen;
        _menu = mnext;
        _rowOpen = rnext;
        _drop = dnext;
        _arrive = anext;
        PollClick(p);
        HandleTrayInteraction(p, down);

        bool startExpand = _progress <= 0.02f && next > 0.02f;
        bool deskChanged = false;
        if (fg != _lastFg || startExpand)
        {
            // the pill follows the session you're inside (skip while a notice/drop owns it)
            if (fg != _lastFg && _drop < 0f && !_agentNotices.IsOpen(now))
                FollowForeground(fg);
            if (fg != _lastFg) FollowForegroundMedia(ProcessNameOf(fg)); // in the player you're looking at → show the other
            _lastFg = fg;
            bool desk = _notch.ProbeBehind(out _behind);
            deskChanged = desk != _lastDesktop;
            _lastDesktop = desk;
            if (deskChanged && !desk) _captureTick = CaptureSlow; // enter app → capture glass this tick
        }

        int captureEvery = _progress > 0.5f ? CaptureFast : CaptureSlow;
        if (_heavy) captureEvery *= 3; // heavy load → refresh the glass far less often
        if (!_lastDesktop && _behind != IntPtr.Zero && ++_captureTick >= captureEvery)
        {
            _captureTick = 0;
            _notch.CaptureFrom(_behind); // async; re-render happens when CaptureVersion bumps
        }
        int cv = _notch.CaptureVersion;
        bool refreshed = cv != _lastCaptureVer;
        _lastCaptureVer = cv;

        // tick once/sec (even collapsed) so the running-turn elapsed time stays live
        bool tick = DateTime.Now.Second != _lastSec;
        _lastSec = DateTime.Now.Second;

        // Anything that says it is animating gets frames — and while the panel is OPEN it gets every frame.
        // This used to be gated on _progress < 0.5f, i.e. on the pill being COLLAPSED, so an open panel only
        // redrew when something else happened to mark it dirty. Measured with a title trying to scroll at
        // 42px/s: 42 distinct frames in 12 seconds, about 3.5fps, which is exactly as choppy as it sounds.
        // AdaptFrameRate already holds a solid 60 while the panel is open, so there is nothing to save here.
        bool forceAnim = false;
        bool animating = _widgets[_primary].Animating;
        if (animating && _progress >= 0.5f) forceAnim = true;                               // open: every frame
        else if (animating && ++_animTick >= 4) { _animTick = 0; forceAnim = true; }         // collapsed: ~30fps

        // cursor in logical panel coords for widget hover effects; redraw as it moves over the open panel
        // (while a banner is up, coords are banner-local instead so its grabber can react to hover)
        bool overNow = _notif != null ? overNotif : hovered && next > 0.98f;
        var mouse = _notif != null
            ? new PointF((p.X - NotifLeft()) / S, (p.Y - _ct) / S)
            : new PointF((p.X - _el) / S, (p.Y - _et) / S);
        bool mouseMoved = WidgetInput.Over != overNow || (overNow && WidgetInput.Mouse != mouse);
        WidgetInput.Over = overNow;
        WidgetInput.Mouse = mouse;
        // held state, so a widget can scrub while the button is down; the click dispatch below is
        // edge-triggered and cannot express "still holding"
        WidgetInput.Down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;

        // swap-circle strip eases in when a second app appears (and out when it goes) — no pop
        float prevStrip = _stripT;
        _stripT = Math.Clamp(_stripT + (AltIndices().Length >= 1 ? 1 : -1) * _dt / 0.22f, 0f, 1f);

        // no-app tuck-away: the pill melts to an invisible (alpha≈1) strip while empty and grows back
        // when a widget wakes. Unlike before it stays SHOWN — a live OLE drop target — so dragging a
        // file onto the notch's home spot still wakes the tray, and there's no dark tab left behind
        // (Task: "سیاهی کوچیک"). An active mic/camera keeps a real slim tab so its privacy dot shows;
        // only fullscreen fully hides the window now.
        float prevShrink = _shrink;
        _shrink = Math.Clamp(_shrink + (_empty ? 1 : -1) * _dt / 0.28f, 0f, 1f);

        int wv = WidgetVersion();
        bool changed = next != _progress || wv != _widgetVersion || deskChanged || wasEmpty != _empty
            || refreshed || tick || _menu != prevMenu || _drop != prevDrop || _arrive != prevArrive
            || _rowOpen != prevRowOpen || forceAnim || mouseMoved || rescaled || _handle != prevHandle
            || _shrink != prevShrink || _stripT != prevStrip || _notifT != prevNotifT || _notifDetail != prevNotifDetail
            || _offsetX != prevOffsetX || _holdT != prevHoldT || !ReferenceEquals(_notif, notifStart);
        _progress = next;
        _widgetVersion = wv;
        if (changed) Apply(_progress);
    }

    // hover region of the strip: the vertical app column + the open row's rightward fan
    private bool InMenu(Win32.POINT p)
    {
        var rows = Groups();
        if (rows.Count == 0) return false;
        int D = Sc(LayeredNotch.CircleD);
        int x = _cl + Sc(CollapsedW + LayeredNotch.CircleGap + LayeredNotch.PrivacyPad);
        float openV = EaseOutBack(Math.Clamp(_menu, 0f, 1f));
        float hNow = D + (rows.Count - 1) * D * Math.Max(0f, openV);
        if (p.X >= x && p.X < x + D && p.Y >= _ct && p.Y < _ct + Math.Max(D, hNow))
            return true;
        if (_row >= 0 && _row < rows.Count && _rowOpen > 0f)
        {
            float ext = rows[_row].Length * D * EaseOutBack(Math.Clamp(_rowOpen, 0f, 1f));
            if (p.X >= x + D && p.X < x + D + ext
                && p.Y >= _ct + _row * D && p.Y < _ct + (_row + 1) * D)
                return true;
        }
        return false;
    }

    // the group circle wears the "most alive" member's ring (a working green beats an idle white)
    private Color? GroupRing(int[] gr)
    {
        Color? first = null;
        foreach (var i in gr)
        {
            if (_widgets[i].Ring is not { } rc) continue;
            first ??= rc;
            if (rc.R != rc.G || rc.G != rc.B) return rc; // first non-grey = an actual state colour
        }
        return first;
    }

    // alt widgets grouped per app (media / claude / codex / each generic agent by name),
    // preserving widget order inside a group
    private List<int[]> Groups()
    {
        var byKind = new Dictionary<string, List<int>>();
        var order = new List<string>();
        foreach (var i in AltIndices())
        {
            string kind = _widgets[i] switch
            {
                MediaWidget => "media",
                VlcWidget => "vlc",
                DownloadWidget => "download",
                FileTray => "filetray",
                ClaudeCodeWidget => "claude",
                CodexWidget => "codex",
                GenericAgentWidget ga => "g:" + ga.GroupKey,
                _ => "other",
            };
            if (!byKind.TryGetValue(kind, out var list)) { list = new List<int>(); byKind[kind] = list; order.Add(kind); }
            list.Add(i);
        }
        return order.ConvertAll(k => byKind[k].ToArray());
    }

    private int[] ActiveIndices()
    {
        var active = new List<int>(_widgets.Length);
        for (int i = 0; i < _widgets.Length; i++)
            if (_widgets[i].IsActive)
                active.Add(i);
        return [.. active];
    }

    // active widgets other than the primary — these fill the swap circle / dropdown
    private int[] AltIndices()
    {
        var act = ActiveIndices();
        int n = 0;
        foreach (var i in act) if (i != _primary) n++;
        var r = new int[n];
        int j = 0;
        foreach (var i in act) if (i != _primary) r[j++] = i;
        return r;
    }

    private int WidgetVersion()
    {
        int v = Privacy.Version; // repaint when mic/camera use toggles (the dot)
        foreach (var wgt in _widgets) v += wgt.Version;
        return v;
    }

    private void PollClick(Win32.POINT p)
    {
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (_moving) { _lastMouseDown = down; return; } // dragging the pill — swallow clicks
        if (down && !_lastMouseDown && !_resizing && _notif != null)
        {
            // no close button by design: a click outside dismisses (softly); the grabber strip
            // grows it into the full message; a click on the banner body opens the source app
            // (like clicking the real toast would) and dismisses.
            var copyR = NotifBanner.CopyRect(_notif, _curW);
            if (!InRect(p, NotifLeft(), _ct, Sc(_curW), Sc(_curH)))
                _notifClosing = true;
            // Copy-code button: copy the detected 2FA code, flash "Copied", keep the banner open
            else if (!copyR.IsEmpty
                && p.X >= NotifLeft() + copyR.X * S && p.X < NotifLeft() + copyR.Right * S
                && p.Y >= _ct + copyR.Y * S && p.Y < _ct + copyR.Bottom * S)
            {
                Halo.Interop.Clipboard.SetText(_notif.Code);
                _notif.Copied = true;
                _notifDeadline = Max(_notifDeadline, DateTime.UtcNow.AddSeconds(2)); // give a beat to see "Copied"
            }
            else if (!_notifDetailOn && _notif.Body.Length > 0 && p.Y >= _ct + Sc(_curH - 22))
            {
                _notifDetailOn = true;
                _notifDeadline = DateTime.MaxValue; // reading the long form — stays until dismissed
            }
            else
            {
                _notif.Activate();
                _notifClosing = true;
            }
        }
        else if (down && !_lastMouseDown && !_resizing)
        {
            if (_progress > 0.9f)
            {
                var pr = PinRect(ExpandedW, ExpandedH);
                if (p.X >= _el + pr.X * S && p.X < _el + (pr.X + pr.Width) * S
                    && p.Y >= _et + pr.Y * S && p.Y < _et + (pr.Y + pr.Height) * S)
                    { _pinned = !_pinned; SavePin(); _notch.SetCapturable(_pinned); }
                else
                // widget rects are logical; the cursor is physical — compare scaled, hand back logical
                foreach (var (r, onClick) in _widgets[_primary].Buttons(ExpandedW, ExpandedH))
                {
                    float bx = _el + r.X * S, by = _et + r.Y * S;
                    if (p.X >= bx && p.X < bx + r.Width * S && p.Y >= by && p.Y < by + r.Height * S)
                    {
                        onClick(new PointF((p.X - _el) / S, (p.Y - _et) / S));
                        break;
                    }
                }
            }
            else if (_progress < 0.1f && TryCollapsedButton(p)) { }
            else if (_progress < 0.1f && ActiveIndices().Length >= 2 && _drop < 0f && InMenu(p))
            {
                var rows = Groups();
                int D = Sc(LayeredNotch.CircleD);
                int mx = _cl + Sc(CollapsedW + LayeredNotch.CircleGap + LayeredNotch.PrivacyPad);
                int row = Math.Clamp((p.Y - _ct) / D, 0, rows.Count - 1);
                var grp = rows[row];
                int rel = (p.X - mx) / D; // 0 = the app row's own circle, 1.. = a fanned session
                int pick = rel <= 0 || grp.Length == 1 ? 0 : Math.Clamp(rel - 1, 0, grp.Length - 1);
                _pending = grp[pick];
                _dropIcon = _widgets[_pending].Icon;
                _dropImage = _widgets[_pending].IconImage;
                int DL = LayeredNotch.CircleD; // drop coords feed the (logical) render space
                _dropCX = rel <= 0 ? DL / 2f : (rel + 0.5f) * DL; // fly from the circle actually clicked
                _dropCY = (row + 0.5f) * DL;
                _drop = 0f;
                _menu = 0f;
                _rowOpen = 0f;
                _row = -1;
            }
        }
        _lastMouseDown = down;
    }

    // File Tray rows: click opens · Ctrl+click toggles selection · drag up/down reorders (inside the panel)
    // · drag out of the panel extracts the file(s) to Explorer / another app. × and "Remove N" are buttons.
    private void HandleTrayInteraction(Win32.POINT p, bool down)
    {
        if (!(_progress > 0.9f && _drop < 0f && !_moving && _notif == null && _widgets[_primary] is FileTray tray))
        {
            if (_trayMode == 1) FileTray.CancelReorder();
            _trayPressPath = null; _trayMode = -1; _lastTrayDown = down; return;
        }

        var local = new PointF((p.X - _el) / S, (p.Y - _et) / S);
        bool inside = InRect(p, _el, _et, Sc(ExpandedW), Sc(ExpandedH));
        bool ctrl = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;

        if (down && !_lastTrayDown) // fresh press
        {
            _trayPressPath = tray.RowPathAt(ExpandedW, ExpandedH, local);
            _trayPressAt = p;
            _trayMode = 0; // pending
            if (_trayPressPath != null && ctrl) { FileTray.ToggleSelect(_trayPressPath); _trayPressPath = null; _trayMode = -1; }
        }
        else if (down && _trayMode == 0 && _trayPressPath != null) // pending → reorder or drag-out?
        {
            int dx = p.X - _trayPressAt.X, dy = p.Y - _trayPressAt.Y;
            if (!inside) StartTrayDragOut();
            else if (dx * dx + dy * dy > 36) // ~6px → begin an in-panel reorder
            {
                _trayMode = 1;
                FileTray.BeginReorder(_trayPressPath);
                FileTray.UpdateReorder(tray.RowIndexAt(ExpandedW, ExpandedH, local));
            }
        }
        else if (down && _trayMode == 1) // reordering — leaving the panel switches to a drag-out
        {
            if (!inside) { FileTray.CancelReorder(); StartTrayDragOut(); }
            else FileTray.UpdateReorder(tray.RowIndexAt(ExpandedW, ExpandedH, local));
        }

        if (!down && _lastTrayDown) // release
        {
            if (_trayMode == 1) FileTray.CommitReorder();
            else if (_trayMode == 0 && _trayPressPath != null) { FileTray.ClearSelection(); FileTray.Open(_trayPressPath); }
            _trayPressPath = null; _trayMode = -1;
        }
        _lastTrayDown = down;
    }

    private void StartTrayDragOut()
    {
        var paths = _trayPressPath != null ? FileTray.SelectionOrRow(_trayPressPath) : Array.Empty<string>();
        _trayMode = 2;
        _trayPressPath = null;
        // blocks in OLE's modal loop until dropped/cancelled; a drop anywhere but back on the pill
        // auto-removes the file(s) from the tray (effect is unreliable — see FileDrag.Out)
        if (paths.Length > 0 && Halo.Interop.FileDrag.Out(paths) && !CursorOverNotch()) FileTray.RemovePaths(paths);
        _trayPressPath = null; _trayMode = -1;
    }

    // was the drag released back onto our own window? (fumbled drop → keep the files)
    private bool CursorOverNotch()
    {
        return Win32.GetCursorPos(out var p) && Win32.GetWindowRect(_notch.Hwnd, out var r)
            && p.X >= r.left && p.X < r.right && p.Y >= r.top && p.Y < r.bottom;
    }

    private static bool InRect(Win32.POINT p, int left, int top, int w, int h)
        => p.X >= left && p.X < left + w && p.Y >= top && p.Y < top + h;

    // A control drawn on the COLLAPSED pill (the download Stop). Same scaling dance as the expanded
    // buttons: widget rects are logical, the cursor is physical, so compare scaled and hand back logical.
    // Returns true when a button consumed the click, so the caller skips the swap-strip handling.
    private bool TryCollapsedButton(Win32.POINT p)
    {
        if (_primary < 0 || _primary >= _widgets.Length || _empty) return false;
        try
        {
            foreach (var (r, onClick) in _widgets[_primary].CollapsedButtons(CollapsedW, CollapsedH))
            {
                float bx = _cl + r.X * S, by = _ct + r.Y * S;
                if (p.X >= bx && p.X < bx + r.Width * S && p.Y >= by && p.Y < by + r.Height * S)
                {
                    onClick(new PointF((p.X - _cl) / S, (p.Y - _ct) / S));
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    internal static Bitmap? MenuRowImage(IWidget[] widgets, int[] group)
    {
        if (group.Length == 0) return null;
        if (group.Length < 2) return widgets[group[0]].IconImage;
        return widgets[group[0]] switch
        {
            ClaudeCodeWidget => ClaudeCodeWidget.PlainIcon,
            CodexWidget => CodexWidget.PlainIcon,
            _ => widgets[group[0]].IconImage,
        };
    }

    internal static float MenuRowImageOffset(IWidget[] widgets, int[] group)
        => group.Length == 0 ? 0f : widgets[group[0]].IconOffsetX;

    private void Apply(float t)
    {
        float e = EaseOutBack(t);
        int w = (int)Lerp(CollapsedW, ExpandedW, e);
        int h = (int)Lerp(CollapsedH, ExpandedH, e);
        int r = (int)Lerp(CollapsedR, ExpandedR, e);
        if (_shrink > 0f) // no app running: the pill tucks away into a slim tab at the top
        {
            float s = SmoothStep(_shrink);
            w = (int)Lerp(w, 96, s);
            h = (int)Lerp(h, 12, s);
            r = (int)Lerp(r, 6, s);
        }
        bool glass = !_lastDesktop;
        int cT = glass ? TintAppCollapsed : TintDeskCollapsed;
        int eT = glass ? TintAppExpanded : TintDeskExpanded;
        int tint = (int)Lerp(cT, eT, t);
        // empty & idle (no privacy dot to show): fade the slim tab down to an alpha≈1 strip — invisible
        // to the eye but still a live OLE hit-target, so a dragged file wakes the tray at the notch home.
        if (_empty && !Privacy.Active)
            tint = (int)Lerp(tint, EmptyCatchAlpha, SmoothStep(_shrink));
        float fade = Math.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
        float mini = Math.Clamp(1f - t / 0.35f, 0f, 1f); // collapsed preview: full when collapsed, gone by t=0.35
        if (_notif != null && _notifT > 0f) // pill → banner morph rides on top of whatever size it had
        {
            float en = EaseOutBack(_notifT); // the soft overshoot IS the feel — keep it
            float nh = Lerp(NotifBanner.SummaryH, _notifDetailH, SmoothStep(_notifDetail));
            w = (int)Lerp(w, NotifBanner.W, en);
            h = (int)Lerp(h, nh, en);
            r = (int)Lerp(r, 26, en);
            tint = (int)Lerp(cT, eT, _notifT);
            fade = Math.Clamp((_notifT - 0.45f) / 0.55f, 0f, 1f); // banner content fade
            mini *= Math.Clamp(1f - _notifT / 0.35f, 0f, 1f);     // collapsed preview melts away
        }
        float arrive = _arrive < 0f ? 1f : 1f - (1f - _arrive) * (1f - _arrive); // easeOutQuad bloom after swap
        mini *= arrive;

        var groups = _empty ? new List<int[]>() : Groups();
        var frame = new MenuFrame
        {
            Show = groups.Count >= 1 || _stripT > 0.01f, // keep drawing through the ease-out
            Appear = SmoothStep(_stripT),
            // an app with several sessions shows its plain mark on the row; the fan carries the badges
            RowIcons = groups.ConvertAll(gr => _widgets[gr[0]].Icon).ToArray(),
            RowImages = groups.ConvertAll(gr => MenuRowImage(_widgets, gr)).ToArray(),
            RowImageOffsets = groups.ConvertAll(gr => MenuRowImageOffset(_widgets, gr)).ToArray(),
            RowCounts = groups.ConvertAll(gr => gr.Length >= 2 ? gr.Length : 0).ToArray(),
            SessIcons = groups.ConvertAll(gr => gr.Length >= 2
                ? Array.ConvertAll(gr, i => _widgets[i].Icon) : Array.Empty<string>()).ToArray(),
            SessImages = groups.ConvertAll(gr => gr.Length >= 2
                ? Array.ConvertAll(gr, i => _widgets[i].IconImage) : Array.Empty<Bitmap?>()).ToArray(),
            RowRings = groups.ConvertAll(GroupRing).ToArray(),
            RowProgress = groups.ConvertAll(gr => _widgets[gr[0]].RingProgress).ToArray(),
            // duplicates: same state = same hue, but each next session's ring is deeper/darker
            SessRings = groups.ConvertAll(gr => gr.Length >= 2
                ? gr.Select((i, j) => (Color?)(_widgets[i].Ring is { } rc ? Fx.Shade(rc, j) : null)).ToArray()
                : Array.Empty<Color?>()).ToArray(),
            Open = EaseOutBack(Math.Clamp(_menu, 0f, 1f)),
            OpenRow = _row,
            RowOpen = EaseOutBack(Math.Clamp(_rowOpen, 0f, 1f)),
            Dropping = _drop >= 0f,
            DropIcon = _dropIcon,
            DropImage = _dropImage,
            Drop = _drop >= 0f ? _drop : 0f,
        };
        frame.Outward = _dropOut;
        if (frame.Dropping)
        {
            float circleX = w + LayeredNotch.CircleGap + LayeredNotch.PrivacyPad + _dropCX;
            float circleY = LayeredNotch.CircleY + _dropCY;
            float pillX = w - h / 2f, pillY = h / 2f; // pill's rounded end (metaball dominates)
            (frame.FromX, frame.FromY, frame.ToX, frame.ToY) = _dropOut
                ? (pillX, pillY, circleX, circleY)  // arrival: blob detaches from the pill into the circle
                : (circleX, circleY, pillX, pillY); // swap: picked circle fuses into the pill
        }
        // no active widget → bare glass pill (still visible after boot, just a slim tab)
        Action<Graphics, int, int, float> content = _notif is { } toast && _notifT > 0f
            ? (g, cw, ch, f) => NotifBanner.Draw(g, cw, ch, f, toast, SmoothStep(_notifDetail), _notifDetailOn)
            : _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawContent;
        bool pin = _notif == null; // the pin has no business on a notification banner
        _curW = w;
        _curH = h;
        _notch.OffsetX = _offsetX; // where the pill is parked (drag-to-move)
        float holdCue = _moving ? 0f : _holdT;
        // The glass layer has to fade out with the tint. It used to be drawn at full opacity whatever the
        // tint was, so when the last app closed (a VLC video ending, say) the "invisible" catch-strip kept
        // painting a blurred picture of the desktop behind it — a small grey rectangle that looked like it
        // was colour-matching the wallpaper because it *was* the wallpaper.
        float glassFade = _empty && !Privacy.Active ? 1f - SmoothStep(_shrink) : 1f;
        _notch.Render(w, h, r, tint, fade, mini, glass, frame,
            (g, cw, ch, f) => { content(g, cw, ch, f); if (pin) DrawPin(g, cw, ch, f); if (holdCue > 0.01f) DrawHoldCue(g, cw, ch); },
            _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawCollapsed,
            glassFade);
    }

    // focusing a window that hosts a live agent's process/console makes that widget primary,
    // so the pill always mirrors the session you're actually inside
    private void FollowForeground(IntPtr fg)
    {
        try
        {
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return;
            for (int i = 0; i < _widgets.Length; i++)
            {
                if (i == _primary || !_widgets[i].IsActive) continue;
                foreach (var owner in _widgets[i].OwnerPids)
                    if (owner == (int)pid)
                    {
                        _primary = i;
                        _agentNotices.SetPrimary(i);
                        return;
                    }
            }
        }
        catch { }
    }

    // media complement: if the player you just focused is the one already shown big (primary), hand the
    // pill off to another active player — you can already see/control the one you're inside. No OwnerPids
    // for media (GSMTC hides the pid), so we match by process name instead of the FollowForeground ancestry.
    private void FollowForegroundMedia(string fgProc)
    {
        if (string.IsNullOrEmpty(fgProc)) return;
        if (_widgets[_primary] is not MediaWidget pm || !pm.IsActive || !AppMatches(pm.App, fgProc)) return;
        for (int i = 0; i < _widgets.Length; i++)
            if (i != _primary && _widgets[i] is MediaWidget m && m.IsActive)
            { _primary = i; _agentNotices.SetPrimary(i); return; }
    }

    // loose match of a media app id ("spotify", "msedge") against a foreground process name ("Chrome")
    private static bool AppMatches(string app, string proc)
    {
        proc = proc.ToLowerInvariant();
        return app.Length > 1 && proc.Length > 1 && (app == proc || app.Contains(proc) || proc.Contains(app));
    }

    // pid → parent pid for the whole system, refreshed at most every 2s (process trees are stable)
    private Dictionary<int, int> _parentMap = new();
    private long _parentMapAt;
    private Dictionary<int, int> ParentMap()
    {
        long now = Environment.TickCount64;
        if (_parentMap.Count > 0 && now - _parentMapAt < 2000) return _parentMap;
        var snap = Win32.CreateToolhelp32Snapshot(Win32.TH32CS_SNAPPROCESS, 0);
        if (snap == new IntPtr(-1)) return _parentMap;
        try
        {
            var map = new Dictionary<int, int>(512);
            var pe = new Win32.PROCESSENTRY32W
            { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.PROCESSENTRY32W>() };
            if (Win32.Process32FirstW(snap, ref pe))
                do { map[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID; }
                while (Win32.Process32NextW(snap, ref pe));
            if (map.Count > 0) { _parentMap = map; _parentMapAt = now; }
        }
        finally { Win32.CloseHandle(snap); }
        return _parentMap;
    }

    // does the focused window (fgPid) host this widget's process? i.e. fgPid is an ancestor of an owner
    // pid — the WindowsTerminal / VS Code window a Claude session runs inside. Walks the parent chain.
    private bool FgHostsWidget(int fgPid, int widget)
    {
        if (fgPid <= 4) return false;
        var map = ParentMap();
        foreach (var owner in _widgets[widget].OwnerPids)
        {
            int p = owner, guard = 0;
            while (p > 4 && guard++ < 32)
            {
                if (p == fgPid) return true;
                if (!map.TryGetValue(p, out p)) break;
            }
        }
        return false;
    }

    // creative pushpin in the panel's top-left corner: an angled thumbtack — glossy amber head +
    // short needle when pinned, a dim outline when not. Big head / short needle / 28° tilt so it
    // reads as a tack, not a balloon or a sewing needle. Instant on/off (see PollClick); hover
    // shows a tiny English label to the right.
    private static RectangleF PinRect(int w, int h) => new(9, 4, 24, 24);

    private void DrawPin(Graphics g, int w, int h, float a)
    {
        if (a <= 0.01f) return;
        var r = PinRect(w, h);
        bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
        _pinHov = Toward(_pinHov, hov ? 1f : 0f, _dt / 0.10f);
        float hv = _pinHov * _pinHov * (3f - 2f * _pinHov);
        DrawPushpin(g, r, _pinned, hv, a);
        if (hv > 0.02f) // hover: tiny English label to the right saying what a click does
        {
            using var f = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);
            using var b = new SolidBrush(Color.FromArgb((int)(200 * hv * a), 235, 235, 235));
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(_pinned ? "unpin" : "pin on top", f, b,
                new RectangleF(r.Right + 6, r.Y, 120, r.Height), sf);
        }
    }

    // the pin art itself — static so the `--render-pin` dev hook can draw it in isolation.
    internal static void DrawPushpin(Graphics g, RectangleF r, bool pinned, float hover, float a)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var st = g.Save();
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 24f * 0.7f; // 0.7 = smaller, centred
        g.TranslateTransform(cx, cy);
        g.RotateTransform(28f);              // the tilt is what makes it a pushpin, not a location pin
        float hr = 6.4f * u;                 // head radius (dominant); needle stays short below it
        var head = new RectangleF(-hr, -3f * u - hr, hr * 2, hr * 2);
        using var needle = new GraphicsPath();
        needle.AddPolygon(new[] { new PointF(-2.3f * u, 2.5f * u), new PointF(2.3f * u, 2.5f * u), new PointF(0, 12f * u) });
        if (pinned)
        {
            var amber = Color.FromArgb((int)(255 * a), 255, 200, 92);
            using (var nb = new SolidBrush(amber)) g.FillPath(nb, needle);
            using (var hp = new GraphicsPath())
            {
                hp.AddEllipse(head);         // glossy head: bright top-left → amber, like a lit dome
                using var pgb = new PathGradientBrush(hp)
                {
                    CenterPoint = new PointF(head.X + hr * 0.62f, head.Y + hr * 0.62f),
                    CenterColor = Color.FromArgb((int)(255 * a), 255, 236, 182),
                    SurroundColors = new[] { amber },
                };
                g.FillPath(pgb, hp);
            }
            using var gloss = new SolidBrush(Color.FromArgb((int)(115 * a), 255, 255, 255));
            g.FillEllipse(gloss, head.X + hr * 0.28f, head.Y + hr * 0.26f, hr * 0.8f, hr * 0.8f);
        }
        else
        {
            int dim = (int)((122 + 78 * hover) * a);
            using var pen = new Pen(Color.FromArgb(dim, 255, 255, 255), 1.7f * u)
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, needle);
            g.DrawEllipse(pen, head.X, head.Y, hr * 2, hr * 2);
        }
        g.Restore(st);
    }

    private static float Toward(float v, float t, float step)
        => v < t ? Math.Min(t, v + step) : Math.Max(t, v - step);
    // a compact cancelled with Esc fires no hook — watch for the keystroke ourselves while the
    // agent's host window is foreground. Wrong guesses self-heal: post-compact still fires on a
    // real completion and brings the "compacted :)" notice with it.
    private void DetectCompactCancel(IntPtr fg)
    {
        if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) == 0) return;
        bool claude = _claudeStore.Current?.State == "compacting";
        bool codex = _codexStore.Current?.State == "compacting";
        if (!claude && !codex || !ForegroundIsAgentHost(fg)) return;
        if (claude) ClaudeCodeWidget.MarkCompactCancelled(_claudeStore.Current?.StartedAt);
        if (codex) CodexWidget.MarkCompactCancelled(_codexStore.Current?.StartedAt);
    }

    private static string ProcessNameOf(IntPtr hwnd)
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private static bool ForegroundIsAgentHost(IntPtr fg)
    {
        try
        {
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return false;
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = proc.ProcessName.ToLowerInvariant();
            return name is "windowsterminal" or "wt" or "conhost" or "openconsole" or "powershell"
                or "pwsh" or "cmd" or "bash" or "wsl" or "alacritty" or "wezterm-gui" or "code"
                or "chatgpt" or "codex" || name.Contains("claude");
        }
        catch
        {
            return false;
        }
    }

    private void CancelClaude(int slot)
    {
        var pid = _claudeStore.SessionLive(slot)?.Pid ?? 0;
        if (pid > 0) CcCancel.Request(pid);
    }

    private void CancelCodex(CodexSurface surface)
    {
        var snapshot = _codexStore.Candidate(surface);
        if (snapshot is { Source: CodexSurface.Cli, State: "working", ConsolePid: > 0 })
            CcCancel.Request(snapshot.ConsolePid);
        else if (snapshot is { Source: CodexSurface.Desktop, State: "working" })
            _codexDesktopRuntime.TryCancel();
    }

    // a screenshot hit the clipboard (PrtSc / Win+Shift+S) — Windows never delivers that toast to any
    // third party (not the listener, notification DB, or transient table), so we build the banner from
    // the clipboard image: show the capture as the preview, click opens the saved PNG. Fires on the UI
    // thread (WndProc), same as OnTick. ponytail: temp PNGs live in %TEMP%, which Windows reaps itself.
    private void OnClipboardImage(Bitmap shot, bool isScreenshot)
    {
        string path = "";
        try
        {
            path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"halo-shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch { path = ""; }
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = isScreenshot ? Halo.Notifications.NotifItem.ScreenshotApp : Halo.Notifications.NotifItem.ClipboardApp,
            Title = isScreenshot ? Halo.Notifications.NotifItem.ScreenshotTitle : Halo.Notifications.NotifItem.ImageCopiedTitle,
            Preview = shot,       // the actual capture, shown as a 16:9 thumbnail in the banner
            LaunchPath = path,    // click → open the saved image
            // this was the one banner shipping no icon at all: the preview takes the icon slot, so the
            // badge was simply never drawn and Fx.AccentOf(null) left it the only banner with no glow
            Icon = isScreenshot ? ShotBadge() : ClipBadge(),
        });
    }

    // Alt+Shift keyboard-language switch → a banner naming the new language. Windows sends
    // WM_INPUTLANGCHANGE only to the focused app, so we poll the foreground thread's layout. Only a
    // change while the SAME window stays focused counts as a deliberate switch (Alt-Tabbing to an app
    // with a different layout isn't one).
    private void DetectLanguageChange(IntPtr fg)
    {
        try
        {
            uint tid = Win32.GetWindowThreadProcessId(fg, out _);
            if (tid == 0) return;
            uint lang = (uint)(Win32.GetKeyboardLayout(tid).ToInt64() & 0xFFFF);
            if (lang == 0) return;
            long now = Environment.TickCount64;
            if (fg != _langFg) // switched apps: adopt the new window's layout silently, don't notify
            {
                _langFg = fg; _lastLangId = lang; _langFgSince = now;
                return;
            }
            // same window + real change = Alt+Shift. But Windows applies a per-app input language a beat
            // AFTER focus lands, which looks identical to an in-window switch — ignore the first ~600ms.
            if (_lastLangId != 0 && lang != _lastLangId && now - _langFgSince > 600) ShowLanguageNotif(lang);
            _lastLangId = lang;
        }
        catch { }
    }

    private void ShowLanguageNotif(uint langId)
    {
        string name = "Keyboard", code = "?";
        try
        {
            var ci = new System.Globalization.CultureInfo((int)langId);
            var lang = ci.Parent.EnglishName.Length > 0 ? ci.Parent.EnglishName : ci.EnglishName; // english, region-less
            if (lang.Length > 0) name = lang;
            code = ci.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch { }
        var item = new Halo.Notifications.NotifItem
        {
            App = "Keyboard", Title = name, Icon = LangBadge(code),
            Kind = "language", Duration = 1, // flips are glances — 1s, and a rapid burst shouldn't queue
        };
        // rapid Alt+Shift: swap the banner that's already showing (instant, no re-morph) instead of
        // stacking a backlog behind it; otherwise clear any queued language banners and enqueue fresh.
        if (_notif is { Kind: "language" } && !_notifClosing)
        {
            _notif.Icon?.Dispose();
            _notif = item;
            _notifDeadline = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        _notifSrc.DropPending("language");
        _notifSrc.EnqueueLocal(item);
    }

    // rounded badge with the language's 2-letter code (EN / FA …). Vivid gradient whose hue is derived
    // from the code so each language is distinct AND the banner's glow (Fx.AccentOf) has a real colour.
    private static Bitmap LangBadge(string code)
    {
        var b = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        int hue = ((code.Length > 0 ? code[0] : 'A') * 37 + (code.Length > 1 ? code[1] : 0) * 17) % 360;
        using (var lg = new LinearGradientBrush(new RectangleF(3, 3, 58, 58),
                   Fx.HsvToRgb(hue, 0.60f, 0.96f), Fx.HsvToRgb((hue + 20) % 360, 0.72f, 0.78f), 90f))
        using (var p = Fx.Rounded(new RectangleF(3, 3, 58, 58), 17f))
            g.FillPath(lg, p);
        using var f = new Font("Segoe UI Semibold", 25f, GraphicsUnit.Pixel);
        using var wb = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(code, f, wb, new RectangleF(0, 0, 64, 64), sf);
        return b;
    }

    // fun badge for Halo's OWN system notifs that ship no app icon (battery / network / usage / clock /
    // cpu). Same recipe as LangBadge — a vivid rounded gradient tile + a Fluent glyph — so the banner's
    // glow (Fx.AccentOf) still picks up a real colour instead of the plain letter-in-a-ring fallback.
    // Glyph centred by ink bounds (metric-centred Fluent glyphs read visibly off).
    private static readonly FontFamily BadgeGlyphFont = new("Segoe Fluent Icons");
    private static Bitmap LocalBadge(int glyphCp, int hue, float glyphPx = 30f)
    {
        var b = new Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var lg = new LinearGradientBrush(new RectangleF(3, 3, 58, 58),
                   Fx.HsvToRgb(hue, 0.62f, 0.96f), Fx.HsvToRgb((hue + 24) % 360, 0.74f, 0.78f), 90f))
        using (var p = Fx.Rounded(new RectangleF(3, 3, 58, 58), 17f))
            g.FillPath(lg, p);
        using var path = new GraphicsPath();
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        path.AddString(((char)glyphCp).ToString(), BadgeGlyphFont, (int)FontStyle.Regular, glyphPx, PointF.Empty, sf);
        path.Flatten();
        var gb = path.GetBounds();
        if (gb.Width > 0 && gb.Height > 0)
        {
            using var m = new Matrix();
            m.Translate(MathF.Round(32f - gb.Width / 2f - gb.X), MathF.Round(32f - gb.Height / 2f - gb.Y));
            path.Transform(m);
            using var wb = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
            g.FillPath(wb, path);
        }
        return b;
    }

    // glyphs verified via --render-notif (no tofu). hue sets the tile colour + the banner glow.
    private static Bitmap BatteryBadge() => LocalBadge(0xE996, 12);   // BatterySaver — amber/red
    private static Bitmap NetBadge()     => LocalBadge(0xEB5E, 5, 34f);// WifiWarning — red
    private static Bitmap BtBadge()      => LocalBadge(0xE702, 215);   // Bluetooth — blue
    private static Bitmap LimitBadge()   => LocalBadge(0xE9D9, 285);   // Speed/gauge — purple
    private static Bitmap ClockBadge()   => LocalBadge(0xE917, 205);   // Recent (clock) — blue
    private static Bitmap CpuBadge()     => LocalBadge(0xE950, 28);       // Processor icon tile
    private static Bitmap ShotBadge()    => LocalBadge(0xE722, 200, 28f);// Camera — blue
    private static Bitmap ClipBadge()    => LocalBadge(0xE8C8, 155, 28f);// Copy — teal

    // dev-only: the generated notif badges in a row, for a tofu eyeball via --render-badges
    internal static Bitmap[] AllLocalBadges() => new[]
        { BatteryBadge(), NetBadge(), LimitBadge(), ClockBadge(), CpuBadge(), ShotBadge(), ClipBadge() };

    // dev-only: the banners Halo raises itself, for --render-local. They live here, beside the badge
    // factories and the real EnqueueLocal calls, because the alignment bug this hook exists to catch was
    // invisible for months — every hook rendered a MIRRORED toast, which always has a body, and it is the
    // body-less ones that were broken. Kept next to the originals so the two cannot drift apart quietly.
    internal static Halo.Notifications.NotifItem[] SampleLocalNotices(Bitmap shot) => new[]
    {
        new Halo.Notifications.NotifItem
        {
            App = Halo.Notifications.NotifItem.ScreenshotApp,
            Title = Halo.Notifications.NotifItem.ScreenshotTitle,
            Preview = shot, Icon = ShotBadge(),
        },
        new Halo.Notifications.NotifItem { App = "Network", Title = "Bad internet :/", Icon = NetBadge() },
        new Halo.Notifications.NotifItem
        {
            App = "System", Title = "High CPU usage — 92%", Body = "chrome.exe is using the most.",
            Icon = CpuBadge(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "Claude", Title = "Claude usage 85%", Body = "You've used 85% of your weekly limit.",
            Icon = LimitBadge(),
        },
    };

    // banner is centred like the pill; left edge follows its animated width (+ any drag offset)
    private int NotifLeft() => _notch.WorkLeft + (_notch.WorkWidth - Sc(_curW)) / 2 + (int)_offsetX;

    private static readonly string HaloDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");
    private static readonly string OffsetPath = System.IO.Path.Combine(HaloDir, "offset");
    private static readonly string PinPath = System.IO.Path.Combine(HaloDir, "pinned");

    private void LoadOffset()
    {
        try { if (float.TryParse(System.IO.File.ReadAllText(OffsetPath), System.Globalization.CultureInfo.InvariantCulture, out var v)) _offsetX = v; }
        catch { }
        try { _pinned = System.IO.File.ReadAllText(PinPath).Trim() == "1"; } catch { }
    }

    private void SaveOffset()
    {
        try { System.IO.File.WriteAllText(OffsetPath, _offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
    }

    private void SavePin()
    {
        try { System.IO.File.WriteAllText(PinPath, _pinned ? "1" : "0"); } catch { }
    }

    // press-and-hold ~3s on the pill → collapse + follow the cursor; release drops it; parked near the
    // centre it snaps back (magnet). Runs each tick from OnTick with the live cursor + button state.
    // Is the cursor over a control of the currently expanded widget? Buttons() already describes every
    // clickable rect a widget owns, sliders included, so the move gesture can simply stay out of their way.
    private bool PressOnControl(Win32.POINT p)
    {
        if (_progress <= 0.9f || _primary < 0 || _primary >= _widgets.Length) return false;
        try
        {
            foreach (var (r, _) in _widgets[_primary].Buttons(ExpandedW, ExpandedH))
            {
                float bx = _el + r.X * S, by = _et + r.Y * S;
                if (p.X >= bx - 6 * S && p.X < bx + (r.Width + 6) * S
                    && p.Y >= by - 8 * S && p.Y < by + (r.Height + 8) * S) return true;
            }
        }
        catch { }
        return false;
    }

    private void UpdateMove(Win32.POINT p, bool down, bool hovered)
    {
        int centre = _notch.WorkLeft + _notch.WorkWidth / 2;
        const float snap = 55f; // magnet radius around the default centre
        if (_moving)
        {
            if (down)
            {
                float raw = Math.Clamp(p.X - _moveGrabDX - centre,
                    -(_notch.WorkWidth / 2f - Sc(CollapsedW) / 2f - 8), _notch.WorkWidth / 2f - Sc(CollapsedW) / 2f - 8);
                _offsetX = MathF.Abs(raw) < snap ? 0f : raw; // sticks to centre when dragged near the default
            }
            else { if (MathF.Abs(_offsetX) < snap) _offsetX = 0f; _moving = false; _holdT = 0f; SaveOffset(); }
            return;
        }

        // A press that landed on one of the widget's own controls is not a request to move the pill. Holding
        // the seek bar to drag it used to start the move gesture instead and carry the whole pill off to the
        // side — and the new offset is persisted, so it stayed there.
        bool holding = down && hovered && !_resizing && _notif == null && !PressOnControl(p);
        bool still = Math.Abs(p.X - _holdAnchor.X) <= 8 && Math.Abs(p.Y - _holdAnchor.Y) <= 8;
        if (holding && _holdStart != DateTime.MaxValue && still)
        {
            _holdT = Math.Clamp((float)((DateTime.UtcNow - _holdStart).TotalSeconds / HoldSeconds), 0f, 1f);
            if (_holdT >= 1f) { _moving = true; _moveGrabDX = p.X - (int)(centre + _offsetX); _holdStart = DateTime.MaxValue; }
        }
        else if (holding) { _holdStart = DateTime.UtcNow; _holdAnchor = p; _holdT = 0f; } // fresh press or moved → (re)start
        else { _holdStart = DateTime.MaxValue; _holdT = 0f; }
    }

    // soft growing underline that fills over the hold, so the press-to-move gesture is discoverable.
    // eased width + a gradient that melts into nothing at both ends (no hard caps) + gentle opacity.
    private void DrawHoldCue(Graphics g, int w, int h)
    {
        float t = SmoothStep(_holdT);
        float bw = (w - 64) * t;
        if (bw < 3f) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF((w - bw) / 2f, h - 6f, bw, 2.5f);
        using var p = Fx.Rounded(rect, 1.25f);
        using var br = new System.Drawing.Drawing2D.LinearGradientBrush(
            new RectangleF(rect.X - 0.5f, rect.Y, rect.Width + 1f, rect.Height),
            Color.White, Color.White, 0f);
        int peak = 25 + (int)(110 * t);
        br.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend(3)
        {
            Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(peak, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        g.FillPath(br, p);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static float SmoothStep(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.2f;
        const float c3 = c1 + 1f;
        float p = t - 1f;
        return 1f + c3 * MathF.Pow(p, 3f) + c1 * MathF.Pow(p, 2f);
    }
}
