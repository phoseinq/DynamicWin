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
    private const int TintDeskCollapsed = 255;
    internal const int TintDeskExpanded = 245;
    // 48 = the open panel over an app is ~81% window, and that transparency IS the glass. It also means a
    // bright strip behind it (a message bar, a title bar) shows through as a pale block inside the panel.
    // That was tried at 140, which measured better on the offending capture - band spread 13.7 -> 8.3,
    // sharpest edge 1.64 -> 1.00 - and looked wrong: the glass stopped reading as glass. Reverted on that
    // judgement. Frosting the backdrop (see LayeredNotch.Frost) stays and does what it can; more blur is
    // NOT the answer, past ~1/14 the upscale rings and the edge comes back sharper. A near-black panel
    // shows any light it lets through, so the ghost cannot reach zero while the pane stays this clear.
    // This is the knob if that trade is ever revisited.
    // collapsed stays at 120: the small pill has no room to lose contrast under its own content.
    internal const int TintAppCollapsed = 120, TintAppExpanded = 48;
    // The ask banner's own panel, and it is deliberately far clearer than the widget panel's - on BOTH
    // sides, including the desktop, which the rest of the pill keeps near-opaque at 245.
    //
    // The reason is the option rows. They are empty capsules now: they change almost nothing behind them,
    // so whatever the panel shows IS what shows through them. Against a 245 panel that is black, and the
    // capsules read as holes cut in a dark slab - "the pill itself is dark, that is why the capsules have
    // black behind them". A clear capsule needs the panel to be glass too, or there is nothing for it to
    // be clear ABOUT. On the desktop the window's own acrylic blur supplies that (it was simply buried
    // under 245); over an app the captured backdrop does.
    //
    // Everything drawn on this panel therefore carries its own contrast - lit rims on the capsules,
    // a shadow under every line of text - because at these values the panel guarantees none.
    // Low, and lower than anything else in the pill. This tint is a flat black wash over the WHOLE banner,
    // so it is the last thing still painting the option rows dark once the frost squeeze is off them - the
    // rows do not have a black background of their own, they were inheriting the notch's. Everything drawn
    // on top brings its own contrast (lit capsule rims, a shadow under every line of text), so the wash is
    // only here to stop a busy backdrop turning the banner into noise.
    internal const int TintAskDesk = 60, TintAskApp = 34;
    // How much of LayeredNotch's frost squeeze a banner opts out of. The squeeze exists so a bright band
    // behind the WIDGET panel cannot read as a shape inside the pill; a banner has the opposite job, and
    // at full squeeze every backdrop - white bar, dark editor, colourful game - composited to the same
    // dark slab. Not 1: some blur and desaturation is still what separates glass from a hole in the screen.
    internal const float BannerClarity = 0.8f;
    private const float OpenSeconds = 0.30f, CloseSeconds = 0.38f; // open snappier than close. slowed after
    // the _dt fix made these hit their real wall-clock duration (old timer cadence ran them ~2x slower).
    private const float HoldSeconds = 0.75f; // press-and-hold on the pill this long → drag-to-move engages
    // Glass capture cadence, in MILLISECONDS between captures — not in frames, which is what it used to
    // be. As a frame count it rode whatever tier AdaptFrameRate had picked: "every 2 frames" is 20fps at
    // the 40fps this was sized for, but 60fps at the idle 120fps tier, so the collapsed pill re-grabbed
    // and re-blurred the backdrop three times more often than the budget above ever intended. Measured
    // with HALO_GLASS_DEBUG=1: 30.9 captures/s at 6.19ms each = 19.1% of one core, for a pill that was
    // only sitting there. In milliseconds the rate means the same thing at every tier.
    //
    // These are the glass BACKDROP's refresh rate and nothing else. Animation still gets its frames from
    // AdaptFrameRate and IWidget.Animating, so nothing on screen moves less smoothly for this.
    private const int CaptureOpenMs = 16, CaptureCollapsedMs = 50;   // 60fps open, 20fps collapsed
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
    // The answerable banner: a question the hook parked, waiting for a chip to be clicked. It rides the
    // same morph as a notification because it IS the same pill changing shape - only the body and the
    // hit-test differ. A real toast wins the slot: it expires on its own, while a question waits for a
    // human and can afford to come second.
    private readonly AskStore _asks;
    private PendingAsk? _ask;
    private float _askT;
    private int _askH = 120;
    private int _askHover = -1;
    private System.Collections.Generic.List<(RectangleF Rect, Halo.ClaudeCode.AskOption Option)> _askChips = [];
    // non-null while a free-text answer is being composed. The question's own options answer with their
    // label; this one answers with whatever is in here, which the hook passes through verbatim.
    private string? _askTyped;
    private string? _drawnTyped;
    private string _askDraft = "";
    private string? _askDraftNonce;

    // The greeting owns the pill outright while it runs, which is why it is decided once at startup and
    // never re-checked: a greeting that could restart mid-play is a greeting that will.
    private GreetingKind _greet;
    private float _greetT;

    // Drag-to-rank on the swap strip. _stripKinds is what Groups() last laid out, in view order, so the
    // drag can talk about neighbours the user can actually see.
    private readonly StripOrder _stripOrder = StripOrder.Load(StripOrderPath);
    private List<string> _stripKinds = [];
    private int _dragRow = -1;        // row being carried, or -1
    private float _dragFromY;         // where the finger went down
    private float _dragHeld;          // seconds the button has been down on a row
    private float _carryDY;           // logical pixels the carried row is lifted from its slot (chased)
    private float _carryWant;         // where the cursor says it should be
    private float _drawnCarryDY;
    private int _drawnDragRow = -1;
    private float[] _rowShift = [];    // eased displacement per row while something is being carried
    private readonly Halo.Interop.KeyGrab _keys = new();

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
    private long _lastCaptureAt;   // wall clock of the last glass grab; 0 = grab on the next frame
    private int _animTick;
    private int _lastCaptureVer;
    // edge-triggered system alerts (throttled to ~1/s in CheckAlerts); each flag fires once per episode
    private long _alertAt;
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
    public NotchController(LayeredNotch notch)
    {
        _notch = notch;
        _notch.ClipboardImage += OnClipboardImage; // Windows won't deliver the snip toast → mirror it from the clipboard
        _notch.WantsHandCursor = OverPressable;
        _claudeStore = new StatusStore();
        // The asks live beside the status files, so they ride that store's watcher and 1s poll rather than
        // starting a timer here: the hook gives up 300ms after an unacked ask, and the frame loop's own
        // once-a-second tick would miss that window most of the time.
        _asks = new AskStore(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch"));
        _claudeStore.AfterLoad = _asks.Rescan;
        _asks.Rescan();   // a question raised while Halo was starting is still worth answering
        _keys.OnChar = TypedChar;
        _keys.OnKey = TypedKey;

        // Read and stamped in the same breath. If it were stamped when the animation ENDS, a launch closed
        // or crashed halfway through would replay the long introduction on the next one, and the settings
        // panel restarting Halo is exactly the workload that would find that.
        _greet = GreetingGate.Read(GreetedPath);
        GreetingGate.Mark(GreetedPath);
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
        LoadRecordable();
        _notch.SetCapturable(_recordable);
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
    //
    // The tile and the kind follow the resource. They used to be hard-coded to the CPU's, so a memory
    // warning arrived wearing a processor die — and shared a Kind with it, which meant a queued CPU banner
    // and a RAM banner deduped into one.
    private void QueueLoadNotice(string resource, int pct, Func<string?> topProcess, string? fallbackBody)
    {
        bool cpu = resource == "CPU";
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            string? top = topProcess();
            string? body = top != null ? $"{top} is using the most." : fallbackBody;
            if (body == null) return;
            _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
            {
                App = "System", Title = $"High {resource} usage — {pct}%",
                Body = body, Kind = cpu ? "cpu" : "memory", Duration = 7,
                Icon = cpu ? Badges.Cpu() : Badges.Memory(),
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
        // "weekly" was a guess about the second bucket's length that nothing here ever verified; the
        // rollout only tells us the order. Positional names cannot be wrong about a window Codex may
        // not even have.
        CheckLimit("Codex", CodexLimits.PrimaryFrac, CodexLimits.PrimaryReset, "primary");
        CheckLimit("Codex", CodexLimits.SecondaryFrac, CodexLimits.SecondaryReset, "secondary");
        CheckInternet();
        CheckContext();
        CheckHourly();
        Almanac.Poke();   // idempotent: arms the half-hourly weather refresh once, ~20s after launch
    }

    // Latched per session, not per edge: compacting drops the fraction, and a long session would
    // otherwise re-warn every time it climbed back. A NEW session is not in the set, which is exactly the
    // moment the warning becomes useful again.
    private readonly HashSet<string> _ctxWarned = new(StringComparer.Ordinal);
    private readonly List<string> _ctxLive = new();

    private void CheckContext()
    {
        _ctxLive.Clear();
        foreach (var widget in _widgets)
        {
            if (widget is not Widgets.ClaudeCodeWidget cc) continue;
            var (id, frac) = cc.ContextState();
            if (id is null) continue;
            _ctxLive.Add(id);
            if (frac < Widgets.ClaudeCodeWidget.ContextWarnAt)
            {
                _ctxWarned.Remove(id);  // compacted back down: arm it again
                continue;
            }
            if (!_ctxWarned.Add(id)) continue;
            _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
            {
                App = "Claude", Title = $"Context {(int)(frac * 100)}% full",
                Body = "Answers get vaguer from here — /compact when you can.",
                Kind = "ctx-" + id, Duration = 8, Icon = Badges.Context(),
            });
        }
        // a warned session that has since exited would otherwise sit in the set for the life of the
        // process; nothing reads it again, but it is unbounded across a long uptime
        if (_ctxWarned.Count > _ctxLive.Count) _ctxWarned.IntersectWith(_ctxLive);
    }

    // on the hour (2:00, 3:00 …) a small glance banner with the time. Init to the current hour so
    // launching mid-hour doesn't fire, and starting exactly at :00 chimes only once (hour-change guard).
    // The time alone was the one thing the tray clock already says, so the banner carries the rest of the
    // glance: the day, the date in whatever calendar the place keeps, and the place with its temperature
    // (see Almanac), while the SKY rides in the badge rather than costing any of the line. Longer duration
    // now that there is a second line to read.
    private int _chimedHour = DateTime.Now.Hour;
    private void CheckHourly()
    {
        Almanac.SyncZone();   // throttled to once a minute; DateTime.Now below is wrong without it
        var t = DateTime.Now;
        if (t.Minute != 0 || t.Hour == _chimedHour) return;
        _chimedHour = t.Hour;
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = Almanac.Label, Title = Almanac.Headline(t), Body = Almanac.Detail(t),
            Kind = "hourly", Duration = 6, Icon = Badges.Hourly(),
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
                    Almanac.Poke();   // so a demo fired early in a session still has a reading to show
                    _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
                    {
                        App = Almanac.Label, Title = Almanac.Headline(t), Body = Almanac.Detail(t),
                        Kind = "hourly", Duration = 6, Icon = Badges.Hourly(),
                    });
                    break;
            }
        }
        catch { }
    }

    // Two rungs rather than one. A single 20% latch meant a laptop that went 19% → 6% said nothing the
    // second time, which is the moment the warning is actually for; the tier only ever ratchets DOWN while
    // unplugged, so the second banner costs one more interruption and never nags. Plugging in re-arms both.
    private static readonly int[] BatteryTiers = [20, 10];
    private int _battTier = -1;

    private void CheckBattery()
    {
        if (!Win32.GetSystemPowerStatus(out var s)) return;
        bool onBattery = s.ACLineStatus == 0;   // 1 = plugged, 255 = unknown
        int pct = s.BatteryLifePercent;          // 255 = unknown
        if (!onBattery || pct > 100) { _battTier = -1; return; }   // plugged / unknown → re-arm
        int tier = BatteryTier(pct);
        if (tier <= _battTier) { if (tier < 0) _battTier = -1; return; }
        _battTier = tier;
        bool dead = tier >= 1;
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = "Battery", Title = $"Battery {(dead ? "critical" : "low")} — {pct}%",
            Body = "Tap to turn on Power Saver.",
            Kind = "battery", Duration = 8, OnActivate = EnablePowerSaver,
            Icon = dead ? Badges.BatteryDead() : Badges.BatteryLow(),
        });
    }

    // -1 above the first rung, then 0 (low) and 1 (critical). Pure so the ratchet can be tested without a
    // battery to drain.
    internal static int BatteryTier(int pct)
    {
        int t = -1;
        for (int i = 0; i < BatteryTiers.Length; i++) if (pct <= BatteryTiers[i]) t = i;
        return t;
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
            Kind = $"limit-{app}-{window}", Duration = 8,
            Icon = LongWindow(window) ? Badges.LimitLong() : Badges.Limit(),
        });
    }

    // A window measured in days gets a calendar, one you can burn through in an afternoon gets a bolt. The
    // names are positional for Codex ("primary"/"secondary") because nothing here can verify how long its
    // second bucket actually is.
    internal static bool LongWindow(string window) => window is "weekly" or "secondary";

    // Three different pieces of news, which used to be one. Slow was the only one that ever raised a
    // banner, so an outright outage — the case where you want to stop typing and go look at the router —
    // arrived as "Bad internet", if it arrived at all. Ordered by which fact supersedes the others: with
    // no internet at all it is pointless to also say the API is unreachable.
    private string? _netShown;
    private void CheckInternet()
    {
        var trouble = NetTrouble(ClaudeCode.NetMon.NetDown, ClaudeCode.NetMon.ApiDown, ClaudeCode.NetMon.Slow);
        if (trouble == _netShown) return;   // unchanged, or cleared: nothing to say either way
        _netShown = trouble;
        if (trouble is null) return;        // recovered — the pill's own colour already says so
        var item = trouble switch
        {
            "offline" => new Halo.Notifications.NotifItem
            {
                App = "Network", Title = "No internet", Body = "Nothing is getting out right now.",
                Kind = "net", Duration = 7, Icon = Badges.NetDown(),
            },
            "api" => new Halo.Notifications.NotifItem
            {
                App = "Claude", Title = "Claude is unreachable", Body = "Your connection is fine — theirs isn't.",
                Kind = "net", Duration = 7, Icon = Badges.ApiDown(),
            },
            _ => new Halo.Notifications.NotifItem
            {
                App = "Network", Title = "Bad internet", Kind = "net", Duration = 6, Icon = Badges.NetSlow(),
            },
        };
        _notifSrc.EnqueueLocal(item);
    }

    // pure: which of the three the sample deserves, worst first. Null = nothing wrong.
    internal static string? NetTrouble(bool netDown, bool apiDown, bool slow)
        => netDown ? "offline" : apiDown ? "api" : slow ? "slow" : null;

    // Waking is a start too, and the one this machine actually does - a laptop that sleeps instead of
    // shutting down can go weeks without a boot, which is how the hand ended up being something the user
    // had never seen outside a dev run.
    //
    // Detected from the render loop rather than from a power broadcast: the process is frozen while the
    // machine is suspended, so a frame arriving a long wall-clock gap after the last one IS the wake. No
    // window to subscribe with (this one is NOACTIVATE and takes no WM_POWERBROADCAST), no new dependency,
    // and it cannot miss a wake by having slept through the notification itself. _dt is no use for this:
    // it is clamped to 50ms precisely so a resume advances one step instead of leaping.
    internal static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(90);
    private DateTime _lastTickUtc = DateTime.UtcNow;

    private void CheckWake()
    {
        var now = DateTime.UtcNow;
        var gap = now - _lastTickUtc;
        _lastTickUtc = now;
        // a banner, a question or a greeting already on screen owns the pill; the hand waits for the next
        // wake rather than cutting in front of something that has a reader
        if (gap < WakeGap || _greet != GreetingKind.None || _notif != null || _ask != null) return;
        _greet = GreetingKind.Login;
        _greetT = 0f;
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
        EaseRings();
        CheckAlerts();
        var notifStart = _notif; // an in-place banner swap (rapid language flip) must force a redraw
        var fg = Win32.GetForegroundWindow();
        DetectAgentCancel(fg);
        DetectLanguageChange(fg);
        // A pinned pill still does not appear over a fullscreen video, and it is not for want of trying here:
        // asserting HWND_TOPMOST every single frame changed nothing, and neither did dropping
        // WDA_EXCLUDEFROMCAPTURE (both tried, both measured, both reverted - the second one cost the glass
        // its fast path for nothing). Over a fullscreen flip-model surface dwm composites the shell's own
        // z-bands and nothing else, and a band above one belongs to uiAccess-signed apps installed under
        // Program Files - which an unpackaged app in LOCALAPPDATA cannot be. So the once-a-second assert in
        // CheckAlerts stays, which is what recovers a pill buried by ordinary windows, and fullscreen video
        // is left as the platform's answer rather than papered over with something that breaks other things.
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
        // Nothing takes the pill mid-sentence. A toast winning the slot is normally right - it expires on
        // its own while a question waits for a human - but it also tears down the banner the user is
        // typing into, and the language-flip toast fires exactly when someone switches layout to write the
        // answer.
        //
        // Dropped, not held. Holding was tried and only moves the interruption: the flip toast sat in the
        // queue and popped the moment the field closed, which is the least useful time to be told about a
        // layout change made minutes ago. Nothing is lost that the user can't still see - a mirrored toast
        // is a copy, and the original is in Action Center.
        // The greeting runs to the end or gets out of the way; it never shares the pill. A question is the
        // one thing allowed to cut it short - that has a human waiting on it and 20 seconds to live, and
        // "hello" can be missed without cost.
        float prevGreetT = _greetT;
        var prevGreet = _greet;
        CheckWake();
        if (_greet != GreetingKind.None)
        {
            if (_asks.Pending != null) { _greet = GreetingKind.None; _greetT = 0f; }
            else
            {
                float secs = _greet == GreetingKind.Install
                    ? GreetingPlan.InstallSeconds : GreetingPlan.LoginSeconds;
                _greetT += _dt / secs;
                if (_greetT >= 1f) { _greetT = 0f; _greet = GreetingKind.None; }
            }
        }

        if (_askTyped != null || _asks.Pending != null || _greet != GreetingKind.None)
        { while (_notifSrc.Dequeue() is not null) { } }
        else if (_notif == null && !_notifClosing && _progress <= 0.02f && _drop < 0f
            && _notifSrc.Dequeue() is { } item)
        {
            _notif = item;
            _notifDetailOn = false;
            _notifDetail = 0f;
            _notifDetailH = NotifBanner.DetailHeight(item);
            _notifDeadline = DateTime.UtcNow.AddSeconds(item.Duration); // 6s for real toasts; 1s for language flips
        }
        // A question outranks a toast, which is the opposite of how this shipped. The old reasoning - a
        // toast expires on its own, a question waits for a human and can afford to come second - is what
        // starved it in practice: a question is only alive for 20s, and a run of language-flip toasts while
        // the user typed an answer held the slot until the deadline had passed and the terminal had already
        // taken it. Observed live, and the user's report was "the box appeared after I'd answered."
        //
        // So a toast on screen is cut short and the queue behind it is dropped. Nothing is lost that can't
        // still be read: a mirrored toast is a copy, and the original is in Action Centre.
        if (_notif != null && _asks.Pending != null && !_notifDetailOn) _notifClosing = true;
        float prevAskT = _askT;
        int prevAskHover = _askHover;
        var pendingAsk = _notif == null ? _asks.Pending : null;
        if (pendingAsk?.Nonce != _ask?.Nonce)
        {
            EndTyping();   // before _ask moves, so the draft is filed against the question it was written for
            _ask = pendingAsk;
            _askHover = -1;
            // and if this IS that question coming back - a toast borrowed the pill, or it was reopened -
            // the field comes back with the words still in it
            if (_ask != null && _askDraftNonce == _ask.Nonce && _askDraft.Length > 0) BeginTyping();
        }
        // Recomputed every frame, not cached on the nonce changing. Both are arithmetic over constants, and
        // a cached height that drifted out of step with what Draw lays out is exactly what left the last
        // option hanging below the pill's body with the desktop showing through it.
        if (_ask != null)
        {
            _askChips = AskBanner.Chips(_ask, AskBanner.W);
            _askH = AskBanner.Height(_ask, AskBanner.W);
            // the glass strip is 220 tall by default, which a three-option banner outgrows; ask for the
            // taller grab while this is up and hand it back when it closes
            LayeredNotch.WantCaptureHeight(_askH);
        }
        else if (_notif == null) LayeredNotch.WantCaptureHeight(0);
        _askT = Math.Clamp(_askT + (_ask != null ? _dt / 0.24f : -_dt / 0.30f), 0f, 1f);
        if (_ask != null)
        {
            _askHover = -1;
            if (InRect(p, NotifLeft(), _ct, Sc(_curW), Sc(_curH)))
                for (int i = 0; i < _askChips.Count; i++)
                    if (InChip(p, _askChips[i].Rect)) { _askHover = i; break; }
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

        // While an answer is being typed the pill IS the foreground window, which is the one case the
        // follow logic below must not act on: it would re-probe what is behind us and re-pick the primary
        // widget on the strength of our own window.
        //
        // Cancelling on "foreground is not us" was tried and closed the field the instant it opened: the
        // handover takes a frame or two, and one frame where fg is still the old window is normal. A click
        // outside the banner is the honest end condition, and it is where it lives now.
        bool startExpand = _progress <= 0.02f && next > 0.02f;
        bool deskChanged = false;
        if (_askTyped == null && (fg != _lastFg || startExpand))
        {
            // the pill follows the session you're inside (skip while a notice/drop owns it)
            if (fg != _lastFg && _drop < 0f && !_agentNotices.IsOpen(now))
                FollowForeground(fg);
            if (fg != _lastFg) FollowForegroundMedia(ProcessNameOf(fg)); // in the player you're looking at → show the other
            _lastFg = fg;
            bool desk = _notch.ProbeBehind(out _behind);
            deskChanged = desk != _lastDesktop;
            _lastDesktop = desk;
            if (deskChanged && !desk) _lastCaptureAt = 0; // enter app → capture glass this tick
        }

        int captureEveryMs = _progress > 0.5f ? CaptureOpenMs : CaptureCollapsedMs;
        if (_heavy) captureEveryMs *= 3; // heavy load → refresh the glass far less often
        // A backdrop that keeps coming back byte-identical is not worth grabbing at full rate: the hash in
        // DoCapture already stops it forcing a redraw, but the GRAB is the larger cost — on the PrintWindow
        // path (which is what a capturable pill is forced onto) it is ~30ms of waiting on the other app.
        // Measured: 597 of 600 consecutive captures identical while the pill just sat there. Collapsed
        // only, because with the panel open the user is looking at it; and the streak resets on the first
        // real change, so the glass snaps straight back to full rate on a video.
        if (_progress <= 0.5f) captureEveryMs *= Math.Clamp(1 + _notch.StaleStreak / 6, 1, 4);
        if (!_lastDesktop && _behind != IntPtr.Zero && frameNow - _lastCaptureAt >= captureEveryMs)
        {
            _lastCaptureAt = frameNow;
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
            || _offsetX != prevOffsetX || _holdT != prevHoldT || !ReferenceEquals(_notif, notifStart)
            || _askT != prevAskT || _askHover != prevAskHover || _askTyped != _drawnTyped
            || _greetT != prevGreetT || _greet != prevGreet
            // the carried row moves with the cursor and nothing else changes while it does - leaving it out
            // of this test is what made the drag lag: the icon only caught up when some other part of the
            // pill happened to redraw
            || _carryDY != _drawnCarryDY || _dragRow != _drawnDragRow
            // and unconditionally while something is carried: the slide-aside eases on its own after the
            // cursor has stopped, and the cursor test above cannot see that
            || _dragHeld >= DragHold;
        _progress = next;
        _widgetVersion = wv;
        // against the last DRAWN text, not a copy taken earlier in this same frame. Keystrokes arrive
        // between frames, from the keyboard hook, so a within-frame snapshot is always equal to itself and
        // the field rendered its caret and then never updated again.
        _drawnTyped = _askTyped;
        _drawnCarryDY = _carryDY;
        _drawnDragRow = _dragRow;
        if (changed) Apply(_progress);
    }

    // Chip rects come from AskBanner in banner-local logical pixels, so the hit-test is the same geometry
    // the paint used. Measured once when the question changes rather than per frame: MeasureString on a
    // screen DC is not something the render path should be doing sixty times a second.
    private bool InChip(Win32.POINT p, RectangleF r)
        => p.X >= NotifLeft() + r.X * S && p.X < NotifLeft() + r.Right * S
        && p.Y >= _ct + r.Y * S && p.Y < _ct + r.Bottom * S;

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

    // A ring's colour is a state - idle white, working green, out of juice red - and a state flip repainted
    // the circle between two frames, which reads as a glitch rather than as the thing changing. The target
    // still comes from the widget; only the pixels lag, converging on a time constant so it is the same speed
    // at 30fps and at 120. Done here rather than in the widgets because every widget that has a ring wants it,
    // and ClaudeCode/Codex are mirror twins where one edit always has to be made twice.
    private readonly Dictionary<int, Color> _ringShown = new();
    private void EaseRings()
    {
        for (int i = 0; i < _widgets.Length; i++)
        {
            if (_widgets[i].Ring is not { } target) { _ringShown.Remove(i); continue; }
            if (!_ringShown.TryGetValue(i, out var shown)) { _ringShown[i] = target; continue; }
            float k = 1f - MathF.Exp(-_dt / 0.22f);
            _ringShown[i] = Color.FromArgb(
                (int)MathF.Round(shown.A + (target.A - shown.A) * k),
                (int)MathF.Round(shown.R + (target.R - shown.R) * k),
                (int)MathF.Round(shown.G + (target.G - shown.G) * k),
                (int)MathF.Round(shown.B + (target.B - shown.B) * k));
        }
    }
    // the eased colour if one is on file, else whatever the widget says right now
    private Color? RingOf(int i)
        => _widgets[i].Ring is { } target ? (_ringShown.TryGetValue(i, out var c) ? c : target) : null;

    // the group circle wears the "most alive" member's ring (a working green beats an idle white)
    private Color? GroupRing(int[] gr)
    {
        Color? first = null;
        foreach (var i in gr)
        {
            if (RingOf(i) is not { } rc) continue;
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
        // registration order is still the default; this only re-ranks kinds the user has dragged
        _stripKinds = _stripOrder.Apply(order);
        return _stripKinds.ConvertAll(k => byKind[k].ToArray());
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

    private const float DragHold = 0.26f;   // seconds before a press on a row becomes a carry

    // Tap a row and the app jumps into the pill; hold it and drag up or down and it changes rank.
    //
    // The jump used to fire on the press edge, and it cannot any more: a press is the start of both
    // gestures, so neither can be decided until it is over. This is the same trade the pushpin already
    // makes, for the same reason. The cost is that the jump lands on release instead of press, which is
    // tens of milliseconds later and reads as instant; the alternative was starting the drop animation and
    // then taking it back once the press turned out to be a carry.
    private bool UpdateStripGesture(Win32.POINT p, bool down)
    {
        bool live = _progress < 0.1f && ActiveIndices().Length >= 2 && _drop < 0f && _notif == null
                    && _ask == null && _greet == GreetingKind.None;
        int D = Sc(LayeredNotch.CircleD);

        if (down && !_lastMouseDown)
        {
            if (!live || !InMenu(p)) return false;
            _dragRow = Math.Clamp((p.Y - _ct) / D, 0, Math.Max(0, Groups().Count - 1));
            _dragFromY = p.Y;
            _dragHeld = 0f;
            return true;
        }
        if (_dragRow < 0) return false;
        if (!live) { _dragRow = -1; return false; }

        if (down)
        {
            _dragHeld += _dt;
            if (_dragHeld < DragHold) { _carryDY = 0f; return true; }

            // The lift is continuous and the rank is discrete. Only the rank was here at first, so nothing
            // moved until the cursor had crossed a whole row and the icon never felt picked up.
            //
            // Chased rather than snapped. Raw cursor delta was the first version and it stepped, because
            // the mouse is polled once a frame and its position arrives in jumps of several pixels; easing
            // toward it turns each of those into a run of smaller ones. The constant is short enough - 45ms
            // - that it reads as the icon having a little weight rather than as lag.
            _carryWant = (p.Y - _dragFromY) / S;
            _carryDY = Lerp(_carryDY, _carryWant, Math.Clamp(_dt / 0.045f, 0f, 1f));

            // Re-anchored after every step rather than measured from the original press, so carrying a row
            // three places takes three row-heights of travel instead of the cursor having to outrun an
            // ever-growing offset.
            int steps = (int)((p.Y - _dragFromY) / D);
            if (steps != 0 && _dragRow < _stripKinds.Count)
            {
                string kind = _stripKinds[_dragRow];
                if (_stripOrder.Move(_stripKinds, kind, steps))
                {
                    _stripOrder.Save(StripOrderPath);
                    _dragRow = Math.Clamp(_dragRow + steps, 0, _stripKinds.Count - 1);
                    _dragFromY += steps * D;
                    // the slot moved under it, so the lift starts over - but the DRAWN offset is carried
                    // across, or the icon would teleport by a row at the moment the ranks swap
                    _carryWant = (p.Y - _dragFromY) / S;
                    _carryDY -= steps * LayeredNotch.CircleD;
                }
            }
            return true;
        }

        // released
        bool wasTap = _dragHeld < DragHold && Math.Abs(p.Y - _dragFromY) < D / 2;
        int row = _dragRow;
        _dragRow = -1;
        _dragHeld = 0f;
        _carryDY = 0f;   // dropped: it settles into the slot it earned
        if (wasTap && InMenu(p)) JumpToRow(p, row, D);
        return true;
    }

    private void JumpToRow(Win32.POINT p, int row, int D)
    {
        var rows = Groups();
        if (rows.Count == 0) return;
        row = Math.Clamp(row, 0, rows.Count - 1);
        int mx = _cl + Sc(CollapsedW + LayeredNotch.CircleGap + LayeredNotch.PrivacyPad);
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

    private void PollClick(Win32.POINT p)
    {
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (_moving) { _lastMouseDown = down; return; } // dragging the pill — swallow clicks
        if (UpdatePinGesture(p, down)) { _lastMouseDown = down; return; }
        if (UpdateStripGesture(p, down)) { _lastMouseDown = down; return; }
        // The chip click IS the answer, so it is handled before anything else can treat it as a click on
        // the pill. A click anywhere else on this banner does NOTHING on purpose: dismissing a question by
        // brushing past it would silently send it back to the terminal, and the 20s deadline already ends
        // it without needing a gesture that can be made by accident.
        if (down && !_lastMouseDown && !_resizing && _notif == null && _ask is { } ask && _askT > 0.5f)
        {
            bool hitRow = false;
            for (int i = 0; i < _askChips.Count; i++)
                if (InChip(p, _askChips[i].Rect))
                {
                    hitRow = true;
                    // the write-your-own row opens a field instead of answering; every other row is its
                    // own answer, which is the whole point of the banner
                    if (AskBanner.IsOther(_askChips[i].Option)) BeginTyping();
                    else
                    {
                        _asks.Answer(ask, _askChips[i].Option.Label);
                        EndTyping();
                        ClearDraft();
                        _ask = null;
                        _askHover = -1;
                    }
                    break;
                }
            // clicking away puts the field down. It cannot key off losing focus the way a normal text box
            // would - the pill never had the focus to lose.
            if (!hitRow && _askTyped != null && !InRect(p, NotifLeft(), _ct, Sc(_curW), Sc(_curH)))
                EndTyping();
            _lastMouseDown = down;
            return;
        }
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
            // same question the grabber bar asks itself — a strip that expands into nothing is worse than no
            // strip, and the two must not be able to disagree about whether there is more to read
            else if (!_notifDetailOn && NotifBanner.BodyOverflows(_notif) && p.Y >= _ct + Sc(_curH - 22))
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
                // no pin branch here: UpdatePinGesture runs first and returns before this point for any
                // press that landed on the pushpin, because a tap and a hold there mean different things
                // and neither can be decided on the press edge

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
            // strip rows are not decided here any more - see UpdateStripGesture, which runs first
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
    // Answers WM_SETCURSOR: is this screen point on something that would do anything if clicked? It walks
    // the same rects the click dispatch walks, at the same scale, so the pointer can never promise a press
    // the click path would not honour. Runs on the message pump, not the frame timer, so it stays cheap
    // and swallows anything a widget's Buttons() throws rather than killing the window proc.
    private bool OverPressable(Point p)
    {
        try
        {
            if (_empty || _primary < 0 || _primary >= _widgets.Length) return false;
            if (_progress > 0.9f)
            {
                if (Contains(PinRect(ExpandedW, ExpandedH), _el, _et, p)) return true;
                foreach (var (r, _) in _widgets[_primary].Buttons(ExpandedW, ExpandedH))
                    if (Contains(r, _el, _et, p)) return true;
                return false;
            }
            if (_progress < 0.1f)
                foreach (var (r, _) in _widgets[_primary].CollapsedButtons(CollapsedW, CollapsedH))
                    if (Contains(r, _cl, _ct, p)) return true;
            return false;
        }
        catch { return false; }
    }

    // widget rects are logical and the cursor is physical, the same conversion the click path does
    private bool Contains(RectangleF r, int left, int top, Point p)
    {
        float bx = left + r.X * S, by = top + r.Y * S;
        return p.X >= bx && p.X < bx + r.Width * S && p.Y >= by && p.Y < by + r.Height * S;
    }

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
        // the answerable banner uses the same morph; a toast wins the slot, so this runs only when there
        // is no toast and folds away on its own when the question is answered or expires
        if (_notif == null && _ask != null && _askT > 0f)
        {
            float ea = EaseOutBack(_askT);
            w = (int)Lerp(w, AskBanner.W, ea);
            h = (int)Lerp(h, _askH, ea);
            r = (int)Lerp(r, 26, ea);
            tint = (int)Lerp(cT, glass ? TintAskApp : TintAskDesk, _askT);
            fade = Math.Clamp((_askT - 0.45f) / 0.55f, 0f, 1f);
            // the same melt the toast branch does. Without it the collapsed pill keeps drawing straight
            // through the banner - reported as the agent's icon and "cogitating..." sitting on top of the
            // question, which is exactly what it was.
            mini *= Math.Clamp(1f - _askT / 0.35f, 0f, 1f);
        }
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
        // A row slides aside when the carried one has come over it, and slides back when it leaves. Eased
        // toward the target every frame rather than set to it: the displacement IS the feedback, and a row
        // that teleports out of the way tells the user nothing about what is about to happen.
        if (_rowShift.Length != groups.Count) _rowShift = new float[groups.Count];
        bool carrying = _dragHeld >= DragHold && _dragRow >= 0 && _dragRow < groups.Count;
        float at = carrying ? _dragRow + _carryDY / LayeredNotch.CircleD : 0f;
        for (int i = 0; i < _rowShift.Length; i++)
        {
            float target = 0f;
            if (carrying && i != _dragRow)
            {
                if (_dragRow < i && at >= i) target = -LayeredNotch.CircleD;
                else if (_dragRow > i && at <= i) target = LayeredNotch.CircleD;
            }
            _rowShift[i] = Lerp(_rowShift[i], target, Math.Clamp(_dt / 0.11f, 0f, 1f));
        }
        var frame = new MenuFrame
        {
            CarryRow = _dragHeld >= DragHold ? _dragRow : -1,
            CarryDY = _carryDY,
            RowShift = _rowShift,
            Show = _greet == GreetingKind.None && (groups.Count >= 1 || _stripT > 0.01f), // through the ease-out
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
                ? gr.Select((i, j) => (Color?)(RingOf(i) is { } rc ? Fx.Shade(rc, j) : null)).ToArray()
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
        // The greeting is drawn LAST in this chain and sized first above, because it is the one surface
        // that is not showing you anything - there is nothing behind it to fall back to and nothing it can
        // usefully share the pill with.
        if (_greet != GreetingKind.None)
        {
            var gf = _greet == GreetingKind.Install
                ? GreetingPlan.Install(_greetT) : GreetingPlan.Login(_greetT);
            w = (int)gf.PillW;
            h = (int)gf.PillH;
            r = (int)gf.Radius;
            fade = 1f;
            mini = 0f;   // the collapsed widget preview would draw straight through the writing
            // and nothing else may be on screen either. Startup picks a primary widget and plays the
            // arrival bloom for it, and the swap strip appears the moment a second app is live - so an icon
            // came flying in beside the greeting on launch, which is the "one of the icons jumps onto the
            // screen at the start" bug. The greeting is the only thing the pill is saying at that moment.
            _drop = -1f;
            _arrive = -1f;
            _stripT = 0f;
        }
        // no active widget → bare glass pill (still visible after boot, just a slim tab)
        Action<Graphics, int, int, float> content = _greet != GreetingKind.None
            ? (g, cw, ch, f) => DrawGreeting(g, cw, ch)
            : _notif == null && _ask is { } q && _askT > 0f
            ? (g, cw, ch, f) => AskBanner.Draw(g, cw, ch, f, q, _askHover, tint, _askTyped)
            : _notif is { } toast && _notifT > 0f
            ? (g, cw, ch, f) => NotifBanner.Draw(g, cw, ch, f, toast, SmoothStep(_notifDetail), _notifDetailOn)
            : _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawContent;
        bool pin = _notif == null && _ask == null && _greet == GreetingKind.None; // no chrome on a banner
        _curW = w;
        _curH = h;
        _notch.OffsetX = _offsetX; // where the pill is parked (drag-to-move)
        float holdCue = _moving ? 0f : _holdT;
        // The glass layer has to fade out with the tint. It used to be drawn at full opacity whatever the
        // tint was, so when the last app closed (a VLC video ending, say) the "invisible" catch-strip kept
        // painting a blurred picture of the desktop behind it — a small grey rectangle that looked like it
        // was colour-matching the wallpaper because it *was* the wallpaper.
        //
        // A banner is exempt, and that exemption is the whole reason the ask banner looked like a black
        // slab. _empty means the pill has no widget to show - which is the NORMAL state when a question
        // arrives with no agent session on the strip - and it drives _shrink to 1, so glassFade was 0 and
        // the backdrop was never composited: tint over nothing. Not a single trace of the app behind it
        // came through, over any wallpaper, at any tint. The fade is for the invisible drop-catch strip,
        // and a banner is the opposite of that - a full surface, with content on it, asking to be read.
        bool banner = _notif != null || (_ask != null && _askT > 0f);
        float glassFade = _empty && !Privacy.Active && !banner ? 1f - SmoothStep(_shrink) : 1f;
        _notch.Render(w, h, r, tint, fade, mini, glass, frame,
            (g, cw, ch, f) => { content(g, cw, ch, f); if (pin) DrawPin(g, cw, ch, f); if (holdCue > 0.01f) DrawHoldCue(g, cw, ch); },
            _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawCollapsed,
            glassFade, banner ? BannerClarity : 0f);
    }

    // The ink is white at whatever alpha the plan says, over the pill's own glass - it is never tinted to
    // an accent. This is the one moment the pill has no state to report, and a colour would imply one.
    private void DrawGreeting(Graphics g, int w, int h)
    {
        var f = _greet == GreetingKind.Install
            ? GreetingPlan.Install(_greetT) : GreetingPlan.Login(_greetT);
        var box = Greeting.InkBox(w, h);
        // the login hand sits in a 40px pill, so its pen has to be proportionally heavier or it comes out
        // as a hairline scribble
        Greeting.DrawHello(g, box, f.Written, f.HelloAlpha, Color.White,
            _greet == GreetingKind.Install ? 9f : 11f);
        if (f.LineAlpha > 0.004f)
            Greeting.DrawLine(g, Greeting.Lines[f.LineIndex], box, f.LineWritten, f.LineAlpha, Color.White,
                _greet == GreetingKind.Install ? 9f : 11f);
    }

    private void BeginTyping()
    {
        if (_askTyped != null) return;
        _askTyped = _askDraftNonce == _ask?.Nonce ? _askDraft : "";
        _keys.Start();
    }

    // Closing the field is never the same as discarding what is in it. Escape, a toast stealing the pill,
    // the question being re-served by the store - all of them land here, and only actually answering
    // throws the words away.
    private void EndTyping()
    {
        if (_askTyped == null && !_keys.Active) return;
        if (_askTyped != null) { _askDraft = _askTyped; _askDraftNonce = _ask?.Nonce; }
        _askTyped = null;
        _keys.Stop();   // the hook is global; leaving it installed would keep eating the user's keystrokes
    }

    private void ClearDraft()
    {
        _askDraft = "";
        _askDraftNonce = null;
    }

    // Both of these run on the low-level hook callback, which Windows kills the hook for if it takes too
    // long - so they only touch state. The 8ms frame is what notices and redraws, which is a frame away
    // and invisible; calling Apply here rendered the whole banner inside the callback.
    private void TypedChar(char c)
    {
        if (_askTyped == null) return;
        if (c < ' ' || c == 0x7F) return;
        if (_askTyped.Length >= 400) return;   // a banner is not a text editor
        _askTyped += c;
    }

    private void TypedKey(int vk)
    {
        if (_askTyped == null) return;
        if (vk == Win32.VK_BACK)
        {
            if (_askTyped.Length > 0) _askTyped = _askTyped[..^1];
        }
        else if (vk == Win32.VK_ESCAPE) EndTyping();
        else if (vk == Win32.VK_RETURN)
        {
            string answer = _askTyped.Trim();
            // empty is a cancel, not an answer: sending "" would hand Claude a blank reason and look like
            // a choice was made
            if (answer.Length > 0 && _ask is { } ask)
            {
                _asks.Answer(ask, answer);
                _ask = null;
                _askHover = -1;
                EndTyping();
                ClearDraft();
                return;
            }
            EndTyping();
        }
        else if (vk == Win32.VK_V)
        {
            // paste, because the answer people most want to type into a 470px field is one they copied
            try
            {
                if (Clipboard.Text() is { Length: > 0 } t)
                    _askTyped = (_askTyped + t.Replace('\r', ' ').Replace('\n', ' ')).Trim();
            }
            catch { }
        }
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

    private bool OverPin(Win32.POINT p)
    {
        var r = PinRect(ExpandedW, ExpandedH);
        return p.X >= _el + r.X * S && p.X < _el + (r.X + r.Width) * S
            && p.Y >= _et + r.Y * S && p.Y < _et + (r.Y + r.Height) * S;
    }

    // Two gestures on one control, so neither can be settled on the press edge the way a plain button is:
    // a tap pins, a hold decides whether the pill appears in screenshots and recordings. The hold fires the
    // instant the threshold passes rather than on release, so the head lighting up under a finger that is
    // still down IS the confirmation — waiting for release would leave the user holding and guessing. The
    // release that follows is then swallowed, or it would pin as well as toggle capture.
    private DateTime _pinPressAt = DateTime.MaxValue;
    private bool _pinHoldFired;
    private const double PinHoldSeconds = 0.55;

    private bool UpdatePinGesture(Win32.POINT p, bool down)
    {
        bool over = _progress > 0.9f && _notif == null && OverPin(p);
        if (down && !_lastMouseDown)
        {
            if (!over) return false;
            _pinPressAt = DateTime.UtcNow;
            _pinHoldFired = false;
            return true;
        }
        if (_pinPressAt == DateTime.MaxValue) return false;

        if (down)
        {
            if (!_pinHoldFired && (DateTime.UtcNow - _pinPressAt).TotalSeconds >= PinHoldSeconds)
            {
                _pinHoldFired = true;
                _recordable = !_recordable;
                SaveRecordable();
                _notch.SetCapturable(_recordable);
            }
            return true;
        }

        // released: a tap that never reached the hold threshold is the pin toggle. Still has to be over the
        // pin — pressing it and sliding off is how a user takes an accidental press back.
        if (!_pinHoldFired && over) { _pinned = !_pinned; SavePin(); }
        _pinPressAt = DateTime.MaxValue;
        return true;
    }

    // How far into the hold we are, for the art to grow the head with the gesture instead of snapping.
    private float PinHoldProgress()
        => _pinPressAt == DateTime.MaxValue || _pinHoldFired ? 0f
         : Math.Clamp((float)((DateTime.UtcNow - _pinPressAt).TotalSeconds / PinHoldSeconds), 0f, 1f);

    private void DrawPin(Graphics g, int w, int h, float a)
    {
        if (a <= 0.01f) return;
        var r = PinRect(w, h);
        bool hov = WidgetInput.Over && r.Contains(WidgetInput.Mouse);
        _pinHov = Toward(_pinHov, hov ? 1f : 0f, _dt / 0.10f);
        float hv = _pinHov * _pinHov * (3f - 2f * _pinHov);
        DrawPushpin(g, r, _pinned, hv, a, _recordable, PinHoldProgress());
        if (hv > 0.02f) // hover: tiny English label to the right saying what each gesture does
        {
            using var f = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);
            // only the tap is advertised. The hold gesture is deliberately unlabelled.
            string label = _pinned ? "unpin" : "pin on top";
            // On its own chip, because the space to the right of the pin is no longer empty - the agent
            // panels put their stop button at x=42 and the bare text landed on top of it. A hover label
            // that has to be read against whatever it happens to cover is not a label.
            var sz = g.MeasureString(label, f);
            var chip = new RectangleF(r.Right + 6, r.Y + (r.Height - 17) / 2f, sz.Width + 12, 17);
            using (var bgb = new SolidBrush(Color.FromArgb((int)(215 * hv * a), 18, 18, 20)))
            using (var chipPath = Fx.Rounded(chip, 6f))
                g.FillPath(bgb, chipPath);
            using var b = new SolidBrush(Color.FromArgb((int)(230 * hv * a), 235, 235, 235));
            using var sf = new StringFormat(StringFormat.GenericTypographic)
            { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
            g.DrawString(label, f, b, chip, sf);
        }
    }

    // The pin art, and the whole state readout: the pill has no menu, so these three shapes are the only
    // place the two settings are visible.
    //   dim outline      nothing on
    //   fully lit        pinned
    //   lit head only    shows up in screenshots and recordings
    // holdT grows the head while a hold is in progress, so the gesture is visibly doing something well
    // before it fires — a hold with no feedback reads as a click that did not register.
    // static so the `--render-pin` dev hook can draw it in isolation.
    internal static void DrawPushpin(Graphics g, RectangleF r, bool pinned, float hover, float a,
        bool recordable = false, float holdT = 0f)
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
        // a hold nudges the head bigger as it goes; at rest this is exactly 1
        float grow = 1f + 0.18f * holdT;
        if (grow > 1.001f)
        {
            float gh = hr * grow;
            head = new RectangleF(-gh, -3f * u - gh, gh * 2, gh * 2);
            hr = gh;
        }

        if (recordable)
        {
            // head lit, needle left as outline: "in captures", and distinguishable from plain pinned at a
            // glance rather than by remembering which colour meant what.
            // When it is ALSO pinned the needle goes a muted amber instead of white, or the two settings
            // collapse into one picture and tapping to unpin appears to do nothing at all.
            var amber = Color.FromArgb((int)(255 * a), 255, 200, 92);
            if (pinned)
            {
                using var nb = new SolidBrush(Color.FromArgb((int)(150 * a), 255, 200, 92));
                g.FillPath(nb, needle);
            }
            else
            {
                using var pen = new Pen(Color.FromArgb((int)((122 + 78 * hover) * a), 255, 255, 255), 1.7f * u)
                { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawPath(pen, needle);
            }
            using (var hp = new GraphicsPath())
            {
                hp.AddEllipse(head);
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
        else if (pinned)
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
    // Neither a cancelled compact nor a cancelled TURN fires a hook — Claude Code writes status on
    // lifecycle events and an interrupt is not one — so watch for the keystroke ourselves while the
    // agent's host window is foreground. Wrong guesses self-heal: the next real event overwrites the
    // latch, and both latches are keyed by the startedAt they belong to.
    private void DetectAgentCancel(IntPtr fg)
    {
        if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) == 0) return;
        if (!ForegroundIsAgentHost(fg)) return;
        if (_claudeStore.Current?.State == "compacting")
            ClaudeCodeWidget.MarkCompactCancelled(_claudeStore.Current?.StartedAt);
        if (_codexStore.Current?.State == "compacting")
            CodexWidget.MarkCompactCancelled(_codexStore.Current?.StartedAt);
        // the turn case is what left the pill reading "hmm…" indefinitely: nothing else in the system
        // ever learns that an in-flight turn stopped being in flight
        if (_claudeStore.Current?.State == "working")
            ClaudeCodeWidget.MarkTurnCancelled(_claudeStore.Current?.StartedAt);
        if (_codexStore.Current?.State == "working")
            CodexWidget.MarkTurnCancelled(_codexStore.Current?.StartedAt);
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
        var st = _claudeStore.SessionLive(slot);
        var pid = st?.Pid ?? 0;
        if (pid <= 0) return;
        CcCancel.Request(pid);
        // The button injects Esc, so it is the same interrupt the user could have typed — and just as
        // silent. Latch it here too rather than leaning on the keystroke watcher, which only looks
        // while the agent's window is foreground and it plainly is not when the pill was just clicked.
        ClaudeCodeWidget.MarkTurnCancelled(st?.StartedAt);
    }

    private void CancelCodex(CodexSurface surface)
    {
        var snapshot = _codexStore.Candidate(surface);
        if (snapshot is { Source: CodexSurface.Cli, State: "working", ConsolePid: > 0 })
            CcCancel.Request(snapshot.ConsolePid);
        else if (snapshot is { Source: CodexSurface.Desktop, State: "working" })
            _codexDesktopRuntime.TryCancel();
        else return;
        // same silence as the Claude twin: the cancel leaves no lifecycle event behind, so the only record
        // that this turn ended is the latch. Not from the keystroke watcher, which only looks while the
        // agent's window is foreground and it plainly is not when the pill was just clicked.
        CodexWidget.MarkTurnCancelled(snapshot.StartedAt);
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
            Icon = isScreenshot ? Badges.Shot() : Badges.Clip(),
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
            App = "Keyboard", Title = name, Icon = Badges.Language(code),
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

    // dev-only: the banners Halo raises itself, for --render-local. They live here, beside the real
    // EnqueueLocal calls, because the alignment bug this hook exists to catch was invisible for months —
    // every hook rendered a MIRRORED toast, which always has a body, and it is the body-less ones that were
    // broken. Kept next to the originals so the two cannot drift apart quietly.
    internal static Halo.Notifications.NotifItem[] SampleLocalNotices(Bitmap shot) => new[]
    {
        new Halo.Notifications.NotifItem
        {
            App = Halo.Notifications.NotifItem.ScreenshotApp,
            Title = Halo.Notifications.NotifItem.ScreenshotTitle,
            Preview = shot, Icon = Badges.Shot(),
        },
        new Halo.Notifications.NotifItem { App = "Network", Title = "Bad internet", Icon = Badges.NetSlow() },
        new Halo.Notifications.NotifItem
        {
            App = "Network", Title = "No internet", Body = "Nothing is getting out right now.",
            Icon = Badges.NetDown(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "Claude", Title = "Claude is unreachable", Body = "Your connection is fine — theirs isn't.",
            Icon = Badges.ApiDown(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "System", Title = "High CPU usage — 92%", Body = "chrome.exe is using the most.",
            Icon = Badges.Cpu(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "System", Title = "High memory usage — 88%", Body = "Chrome is using the most.",
            Icon = Badges.Memory(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "Battery", Title = "Battery critical — 7%", Body = "Tap to turn on Power Saver.",
            Icon = Badges.BatteryDead(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "Claude", Title = "Context 85% full",
            Body = "Answers get vaguer from here — /compact when you can.", Icon = Badges.Context(),
        },
        new Halo.Notifications.NotifItem
        {
            App = "Claude", Title = "Claude usage 85%", Body = "You've used 85% of your weekly limit.",
            Icon = Badges.LimitLong(),
        },
        // the chime, with a reading it will not have on a fresh process: this is the shape that has to be
        // looked at, since the whole point of the rewrite was how crowded the line was
        new Halo.Notifications.NotifItem
        {
            App = "Tehran",
            Title = Almanac.Headline(DateTime.Today.AddHours(1), new Almanac.Weather(27, 0, Day: false), metric: true),
            Body = Almanac.Detail(DateTime.Today.AddHours(1), CalendarKind.SolarHijri),
            Icon = Badges.Local(0xE708, 232, 32f),
        },
    };

    // banner is centred like the pill; left edge follows its animated width (+ any drag offset)
    private int NotifLeft() => _notch.WorkLeft + (_notch.WorkWidth - Sc(_curW)) / 2 + (int)_offsetX;

    private static readonly string HaloDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo");
    private static readonly string OffsetPath = System.IO.Path.Combine(HaloDir, "offset");
    private static readonly string GreetedPath = System.IO.Path.Combine(HaloDir, "greeted");
    private static readonly string StripOrderPath = System.IO.Path.Combine(HaloDir, "strip-order.txt");
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

    // Off by default, which is the whole point: the common case keeps the cheap screen-DC glass. Turn it on
    // with Ctrl+click on the pushpin when you actually want the pill in a recording (HALO_CAPTURABLE=1 still
    // forces it on for the README gif).
    private static readonly string RecordablePath = System.IO.Path.Combine(HaloDir, "capturable");
    private bool _recordable;

    private void LoadRecordable()
    {
        try { _recordable = System.IO.File.ReadAllText(RecordablePath).Trim() == "1"; } catch { }
    }

    private void SaveRecordable()
    {
        try { System.IO.File.WriteAllText(RecordablePath, _recordable ? "1" : "0"); } catch { }
    }

    // press-and-hold ~3s on the pill → collapse + follow the cursor; release drops it; parked near the
    // centre it snaps back (magnet). Runs each tick from OnTick with the live cursor + button state.
    // Is the cursor over a control of the currently expanded widget? Buttons() already describes every
    // clickable rect a widget owns, sliders included, so the move gesture can simply stay out of their way.
    private bool PressOnControl(Win32.POINT p)
    {
        if (_progress <= 0.9f || _primary < 0 || _primary >= _widgets.Length) return false;
        // the pushpin has a hold gesture of its own; without this, holding it for the capture toggle also
        // ran the pill's press-to-move and carried the pill off sideways mid-gesture
        if (OverPin(p)) return true;
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
        // Everything the File Tray does looks like the move gesture from here — a button held down over the
        // pill that is not travelling far. Dropping a file in filled the hold timer and the pill wandered
        // off mid-drop; so did holding an item to reorder it. The rule is that the pill only offers to move
        // while nothing in the tray is being held: an incoming drag (DragActive), an item under the press
        // (_trayPressPath), or a reorder/drag-out already running (_trayMode) all stand it down. Pressing
        // empty space in the panel still moves the pill, which is the one case that is unambiguous.
        bool holding = down && hovered && !_resizing && _notif == null
                    && !FileTray.DragActive && _trayPressPath == null && _trayMode < 1
                    && !PressOnControl(p);
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
