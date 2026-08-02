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

        bool compacted = notice.CompactedAt is { } doneAt && doneAt != previous.CompactedAt &&
            now - doneAt < TimeSpan.FromSeconds(30);

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

internal sealed partial class NotchController
{
    private const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    private const int ExpandedW = 560, ExpandedH = 220, ExpandedR = 30;
    private const int TintDeskCollapsed = 255;
    internal const int TintDeskExpanded = 245;

    internal const int TintAppCollapsed = 120, TintAppExpanded = 48;

    internal const int TintAskDesk = 60, TintAskApp = 34;

    internal const float BannerClarity = 0.8f;
    private const float OpenSeconds = 0.30f, CloseSeconds = 0.38f;

    private const float HoldSeconds = 0.75f;

    private const int CaptureOpenMs = 16, CaptureCollapsedMs = 50;
    private const int EmptyCatchAlpha = 1;

    private readonly LayeredNotch _notch;
    private readonly StatusStore _claudeStore;
    private readonly CodexStatusStore _codexStore;
    private readonly CodexDesktopRuntime _codexDesktopRuntime;
    private readonly IWidget[] _widgets;
    private readonly MediaSessions _mediaSessions;
    private readonly AgentNoticeCoordinator _agentNotices;
    private readonly DispatcherQueueTimer _timer;

    private float S => _notch.Scale;
    private int Sc(int v) => (int)MathF.Round(v * S);
    private int _cl => _notch.WorkLeft + (_notch.WorkWidth - Sc(CollapsedW)) / 2 + (int)_offsetX;
    private int _el => _notch.WorkLeft + (_notch.WorkWidth - Sc(ExpandedW)) / 2 + (int)_offsetX;
    private int _ct => _notch.WorkTop;
    private int _et => _notch.WorkTop;

    private int _primary;
    private int _userPicked = -1;
    private float _progress;
    private float _menu;
    private float _drop = -1f;
    private float _arrive = -1f;
    private int _pending;
    private float _dropCX, _dropCY;
    private bool _dropOut;
    private string _dropIcon = "";
    private Bitmap? _dropImage;
    private readonly bool[] _prevActive;
    private int _row = -1;
    private float _rowOpen;
    private float _stripT;
    private int _widgetVersion = -1;
    private int _lastSec = -1;
    private bool _lastMouseDown;
    private bool _prevDragActive;
    private long _trayShowUntil;

    private string? _trayPressPath;
    private Win32.POINT _trayPressAt;
    private int _trayMode = -1;
    private bool _lastTrayDown;
    private bool _resizing;
    private Win32.POINT _resizeFrom;
    private float _scale0, _handle;
    private bool _hiddenForFullscreen;

    private float _offsetX;
    private bool _moving;
    private float _holdT;
    private DateTime _holdStart = DateTime.MaxValue;
    private Win32.POINT _holdAnchor;
    private int _moveGrabDX;
    private bool _pinned;

    private static bool Pinned(bool userPin) => userPin || FileTray.Holding;

    private bool TrayFront => FileTray.DragActive || (!_empty && _widgets[_primary] is FileTray);
    private float _pinHov;
    private float _shrink;
    private bool _empty;

    private readonly Halo.Notifications.NotifSource _notifSrc = new();
    private Halo.Notifications.BtBattery? _bt;
    private readonly Widgets.BtWidget _btWidget = new();
    private System.Threading.Timer? _testTrigger;
    private Halo.Notifications.NotifItem? _notif;

    private readonly AskStore _asks;
    private PendingAsk? _ask;
    private float _askT;
    private int _askH = 120;
    private int _askHover = -1;
    private System.Collections.Generic.List<(RectangleF Rect, Halo.ClaudeCode.AskOption Option)> _askChips = [];

    private string? _askTyped;
    private string? _drawnTyped;
    private string _askDraft = "";
    private string? _askDraftNonce;

    private GreetingKind _greet;
    private float _greetT;

    private readonly StripOrder _stripOrder = StripOrder.Load(StripOrderPath);
    private List<string> _stripKinds = [];
    private int _dragRow = -1;
    private float _dragFromY;
    private float _dragHeld;
    private float _carryDY;
    private float _carryWant;
    private float _drawnCarryDY;
    private int _drawnDragRow = -1;
    private float[] _rowShift = [];
    private readonly Halo.Interop.KeyGrab _keys = new();

    private float _notifT;
    private bool _notifClosing;
    private bool _notifDetailOn;
    private float _notifDetail;
    private int _notifDetailH = NotifBanner.SummaryH + 60;
    private DateTime _notifDeadline;
    private int _curW = CollapsedW, _curH = CollapsedH;
    private bool _lastDesktop = true;
    private IntPtr _lastFg = IntPtr.Zero;
    private uint _lastLangId;
    private IntPtr _langFg;
    private long _langFgSince;
    private IntPtr _behind = IntPtr.Zero;
    private long _lastCaptureAt;
    private int _animTick;
    private int _lastCaptureVer;

    private long _alertAt;

    private readonly Dictionary<string, (DateTimeOffset reset, DateTime at)> _limitFired = LoadLimitFired();
    private static readonly string LimitFiredPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "limit-fired.txt");

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
        _notch.ClipboardImage += OnClipboardImage;
        _notch.WantsHandCursor = OverPressable;
        _claudeStore = new StatusStore();

        _asks = new AskStore(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "notch"));
        _claudeStore.AfterLoad = _asks.Rescan;
        _asks.Rescan();
        _keys.OnChar = TypedChar;
        _keys.OnKey = TypedKey;

        _greet = GreetingGate.Read(GreetedPath);
        GreetingGate.Mark(GreetedPath);
        _codexStore = new CodexStatusStore();
        _codexDesktopRuntime = CodexDesktopRuntime.Shared;
        CodexLimits.Attach(_codexStore);
        CodexLimits.UpdateFrom(_codexStore.Current);

        _settings = new Halo.Settings.SettingsStore();
        Halo.Settings.SettingsStore.Shared = _settings;
        _appliedSettings = _settings.Version;
        _api = new Halo.Api.HaloApi(ApiConfig, this);
        _api.Reconcile();
        _startupApplied = _settings.Current.Bool(Halo.Settings.SettingsKeys.StartWithWindows, true);
        _silenceApplied = _settings.Current.Bool("notifications.silence", false);
        if (_silenceApplied) System.Threading.ThreadPool.QueueUserWorkItem(
            _ => { try { Halo.Notifications.BannerGate.Enable(); } catch { } });

        _mediaSessions = new MediaSessions();
        var widgets = new List<IWidget>();
        for (int s = 0; s < MediaSessions.MaxSlots; s++)
            widgets.Add(new MediaWidget(_mediaSessions, s));
        widgets.Add(new VlcWidget(_mediaSessions));
        widgets.Add(new DownloadWidget());
        widgets.Add(new FileTray());
        widgets.Add(_btWidget);
        Privacy.Poke();
        for (int s = 0; s < StatusStore.MaxSessions; s++)
        {
            int slot = s;
            widgets.Add(new ClaudeCodeWidget(_claudeStore, slot, () => CancelClaude(slot)));
        }
        widgets.Add(new CodexWidget(_codexStore, CodexSurface.Desktop, () => CancelCodex(CodexSurface.Desktop),
            () => _codexDesktopRuntime.Presence.Running));
        widgets.Add(new CodexWidget(_codexStore, CodexSurface.Cli, () => CancelCodex(CodexSurface.Cli)));
        var agentStore = GenericAgentWidget.NewStore();
        for (int s = 0; s < StatusStore.MaxSessions; s++)
            widgets.Add(new GenericAgentWidget(agentStore, s));
        _widgets = [.. widgets];

        var active = ActiveIndices();
        LoadOffset();
        LoadRecordable();
        _notch.SetCapturable(_recordable);
        _empty = active.Length == 0;
        _shrink = _empty ? 1f : 0f;
        if (!_empty) _primary = active[0];
        _prevActive = new bool[_widgets.Length];
        for (int i = 0; i < _widgets.Length; i++) _prevActive[i] = Live(i);
        Apply(0f);
        _agentNotices = new AgentNoticeCoordinator(_primary);

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

        try { Frame(); } catch (Exception ex) { CrashLog(ex); }
    }

    private static void CrashLog(Exception ex)
    {
        try
        {
            var p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Halo", "frame-errors.txt");
            System.IO.File.WriteAllText(p, $"{DateTime.Now:HH:mm:ss}\n{ex}");
        }
        catch { }
    }

    private long _cpuIdle, _cpuBusyBase, _cpuAt;
    private int _fps = 120;

    private bool _heavy;

    private static readonly int[] CpuTiers = { 50, 70, 85, 95 };
    private static readonly int[] RamTiers = { 70, 85, 95 };

    internal static int[] Tiers(int[] fixedTiers, int first)
    {
        var tiers = new List<int> { first };
        foreach (var tier in fixedTiers) if (tier > first) tiers.Add(tier);
        return [.. tiers];
    }
    private int _cpuTierFired = -1, _ramTierFired = -1;
    private int _cpuStreak, _ramStreak;
    internal bool Heavy => _heavy;
    private void AdaptFrameRate()
    {
        long now = Environment.TickCount64;
        if (now - _cpuAt < 1000) return;
        _cpuAt = now;
        if (!Win32.GetSystemTimes(out long idle, out long kern, out long user)) return;
        long total = kern + user;

        bool watching = _progress > 0.02f || _notif != null || _drop >= 0f;
        int target = _fps;
        if (_cpuBusyBase != 0 && total > _cpuBusyBase)
        {
            float busy = 1f - (float)(idle - _cpuIdle) / (total - _cpuBusyBase);

            if (watching) target = 60;
            else if (busy > 0.90f) target = 30;
            else if (busy > 0.55f) target = 60;
            else if (busy < 0.45f) target = 120;

            bool heavy = !watching && (_heavy ? busy > 0.40f : busy > 0.50f);
            if (heavy != _heavy)
            {
                _heavy = heavy;
                try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
                    heavy ? System.Diagnostics.ProcessPriorityClass.BelowNormal
                          : System.Diagnostics.ProcessPriorityClass.Normal; } catch { }
            }
            int pctNow = (int)(busy * 100);
            int tier = TierOf(Tiers(CpuTiers, Halo.Settings.SettingsStore.Percent("alert.cpuAt", 50)), pctNow);
            _cpuStreak = tier > _cpuTierFired ? _cpuStreak + 1 : 0;

            if (_cpuStreak >= 10)
            {
                _cpuTierFired = tier; _cpuStreak = 0;
                if (Alert("cpu")) QueueCpuNotice(pctNow);
            }
            CheckRam();
        }
        _cpuIdle = idle; _cpuBusyBase = total;
        if (target != _fps)
        {
            _fps = target;
            _timer.Interval = TimeSpan.FromMilliseconds(target >= 120 ? 8 : target >= 60 ? 16 : 33);
        }
    }

    private static int TierOf(int[] tiers, int pct)
    {
        int t = -1;
        for (int i = 0; i < tiers.Length; i++) if (pct >= tiers[i]) t = i;
        return t;
    }

    private void CheckRam()
    {
        var ms = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref ms)) return;
        int pct = (int)ms.dwMemoryLoad;
        int tier = TierOf(Tiers(RamTiers, Halo.Settings.SettingsStore.Percent("alert.memoryAt", 70)), pct);
        _ramStreak = tier > _ramTierFired ? _ramStreak + 1 : 0;
        if (_ramStreak >= 10)
        {
            _ramTierFired = tier; _ramStreak = 0;
            if (Alert("memory")) QueueRamNotice(pct);
        }
    }

    private void QueueRamNotice(int pct)
        => QueueLoadNotice("memory", pct, TopRamProcess, "Memory is running low.");

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

    private void QueueCpuNotice(int sysPct)
        => QueueLoadNotice("CPU", sysPct, TopCpuProcess, null);

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

    private bool Alert(string name, bool on = true) => _settings.Current.Bool("alert." + name, on);

    private int _appliedSettings = -1;
    private bool _startupApplied;
    private bool _silenceApplied;
    private readonly Halo.Api.HaloApi _api;

    private Halo.Api.HaloApi.Config ApiConfig()
    {
        var current = _settings.Current;
        bool on = current.Bool("api.enabled", false);
        string token = current.Text("api.token", "");
        if (on && token.Length == 0)
        {
            token = Guid.NewGuid().ToString("n");
            _settings.Set("api.token", token);
        }
        return new Halo.Api.HaloApi.Config(
            on,
            int.TryParse(current.Text("api.port", ""), out var port) && port is > 1023 and < 65536
                ? port : Halo.Api.HaloApi.DefaultPort,
            token,
            current.Bool("api.notify", true),
            current.Bool("api.ask", true),
            current.Bool("api.state", false),

            current.Bool("api.control", false),
            current.Bool("api.settings", false));
    }

    private void SyncSettings()
    {
        int version = _settings.Version;
        if (version == _appliedSettings) return;
        _appliedSettings = version;
        var current = _settings.Current;

        bool startup = current.Bool(Halo.Settings.SettingsKeys.StartWithWindows, true);
        if (startup != _startupApplied)
        {
            _startupApplied = startup;
            Autostart(startup);
        }

        bool pin = current.Bool(Halo.Settings.SettingsKeys.OverFullscreen, _pinned);
        if (pin != _pinned)
        {
            _pinned = pin;
            try { System.IO.File.WriteAllText(PinPath, _pinned ? "1" : "0"); } catch { }
        }

        _api.Reconcile();

        bool silence = current.Bool("notifications.silence", false);
        if (silence != _silenceApplied)
        {
            _silenceApplied = silence;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (silence) Halo.Notifications.BannerGate.Enable(); else Halo.Notifications.BannerGate.Restore(); }
                catch { }
            });
        }

        bool recordable = current.Bool(Halo.Settings.SettingsKeys.InCaptures, _recordable);
        if (recordable != _recordable)
        {
            _recordable = recordable;
            try { System.IO.File.WriteAllText(RecordablePath, _recordable ? "1" : "0"); } catch { }
            try { _notch.SetCapturable(_recordable); } catch { }
        }

        if (Scale(current.Text(Halo.Settings.SettingsKeys.Scale, "")) is { } scale
            && Math.Abs(scale - _notch.Scale) > 0.001f)
        {
            _notch.Scale = scale;
            try { _notch.SaveScale(); } catch { }
        }
    }

    private static void Autostart(bool on)
    {
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                string hooks = System.IO.Path.Combine(AppContext.BaseDirectory, "Halo.Hooks.exe");
                if (!System.IO.File.Exists(hooks)) return;
                var psi = new System.Diagnostics.ProcessStartInfo(hooks)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(on ? "install-autostart" : "uninstall-autostart");
                if (on) psi.ArgumentList.Add(Environment.ProcessPath ?? "");
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(20_000);
            }
            catch { }
        });
    }

    private float MotionScale => _settings.Current.Text(Halo.Settings.SettingsKeys.Motion, "Soft") switch
    {
        "Reduced" => 0.35f,
        "Standard" => 1.55f,
        _ => 1f,
    };

    private float GlassScale => _settings.Current.Text(Halo.Settings.SettingsKeys.Glass, "Balanced") switch
    {
        "Light" => 0.66f,
        "Strong" => 1.34f,
        _ => 1f,
    };

    private static float? Scale(string text)
    {
        if (text.Length == 0) return null;
        string digits = text.TrimEnd('%');
        return float.TryParse(digits, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var pct)
            ? Math.Clamp(pct / 100f, 0.7f, 1.6f)
            : null;
    }

    private void CheckAlerts()
    {
        long now = Environment.TickCount64;
        if (now - _alertAt < 1000) return;
        _alertAt = now;
        SyncSettings();
        if (Pinned(_pinned)) _notch.AssertTopmost();

        ReloadOffset();
        if (Alert("battery")) CheckBattery();
        if (Alert("limit"))
        {
            CheckLimit("Claude", ClaudeCode.Limits.FiveHour, ClaudeCode.Limits.FiveHourReset, "5-hour");
            CheckLimit("Claude", ClaudeCode.Limits.Week, ClaudeCode.Limits.WeekReset, "weekly");

            CheckLimit("Codex", CodexLimits.PrimaryFrac, CodexLimits.PrimaryReset, "primary");
            CheckLimit("Codex", CodexLimits.SecondaryFrac, CodexLimits.SecondaryReset, "secondary");
        }
        if (Alert("internet")) CheckInternet();
        if (Alert("context")) CheckContext();
        CheckCompact();
        if (Alert("hourly", on: false)) CheckHourly();
        Almanac.Poke();
    }

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
                _ctxWarned.Remove(id);
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

        if (_ctxWarned.Count > _ctxLive.Count) _ctxWarned.IntersectWith(_ctxLive);
    }

    private void CheckCompact()
    {
        int pid = 0;
        string? key = null;
        for (int s = 0; s < StatusStore.MaxSessions && pid == 0; s++)

            if (_claudeStore.SessionLive(s) is { State: "compacting", Pid: > 0 } st
                && Widgets.ClaudeCodeWidget.Compacting(st))
            {
                pid = st.Pid;
                key = st.StartedAt;
            }
        if (pid > 0) ClaudeCode.CompactProgress.Poke(pid, key);
        else ClaudeCode.CompactProgress.Done();
    }

    private int _chimedHour = DateTime.Now.Hour;
    private void CheckHourly()
    {
        Almanac.SyncZone();
        var t = DateTime.Now;
        if (t.Minute != 0 || t.Hour == _chimedHour) return;
        _chimedHour = t.Hour;
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = Almanac.Label, Title = Almanac.Headline(t), Body = Almanac.Detail(t),
            Kind = "hourly", Duration = 6, Icon = Badges.Hourly(),
        });
    }

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
                    Almanac.Poke();
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

    private static readonly int[] BatteryTiers = [20, 10];

    private static int[] BatteryLadder()
    {
        int low = Halo.Settings.SettingsStore.Percent("alert.batteryAt", 20);
        return low <= 10 ? [low] : [low, 10];
    }
    private int _battTier = -1;

    private void CheckBattery()
    {
        if (!Win32.GetSystemPowerStatus(out var s)) return;
        bool onBattery = s.ACLineStatus == 0;
        int pct = s.BatteryLifePercent;
        if (!onBattery || pct > 100) { _battTier = -1; return; }
        int tier = BatteryTier(pct, BatteryLadder());
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

    internal static int BatteryTier(int pct) => BatteryTier(pct, BatteryTiers);

    internal static int BatteryTier(int pct, int[] ladder)
    {
        int t = -1;
        for (int i = 0; i < ladder.Length; i++) if (pct <= ladder[i]) t = i;
        return t;
    }

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

    private void CheckLimit(string app, float util, DateTimeOffset reset, string window)
    {
        if (util < Halo.Settings.SettingsStore.Percent("alert.limitAt", 80) / 100f) return;
        string key = app + window;
        if (_limitFired.TryGetValue(key, out var f)
            && (DateTime.UtcNow - f.at < TimeSpan.FromHours(6)
                || (reset != default && f.reset != default && (reset - f.reset).Duration() < TimeSpan.FromMinutes(30))))
            return;
        _limitFired[key] = (reset, DateTime.UtcNow);
        SaveLimitFired();
        int p = (int)(util * 100);
        _notifSrc.EnqueueLocal(new Halo.Notifications.NotifItem
        {
            App = app, Title = $"{app} usage {p}%", Body = $"You've used {p}% of your {window} limit.",
            Kind = $"limit-{app}-{window}", Duration = 8,
            Icon = LongWindow(window) ? Badges.LimitLong() : Badges.Limit(),
        });
    }

    internal static bool LongWindow(string window) => window is "weekly" or "secondary";

    private string? _netShown;
    private void CheckInternet()
    {
        var trouble = NetTrouble(ClaudeCode.NetMon.NetDown, ClaudeCode.NetMon.ApiDown, ClaudeCode.NetMon.Slow);
        if (trouble == _netShown) return;
        _netShown = trouble;
        if (trouble is null) return;
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

    internal static string? NetTrouble(bool netDown, bool apiDown, bool slow)
        => netDown ? "offline" : apiDown ? "api" : slow ? "slow" : null;

    internal static readonly TimeSpan WakeGap = TimeSpan.FromSeconds(90);
    private DateTime _lastTickUtc = DateTime.UtcNow;

    private void CheckWake()
    {
        var now = DateTime.UtcNow;
        var gap = now - _lastTickUtc;
        _lastTickUtc = now;

        if (gap < WakeGap || _greet != GreetingKind.None || _notif != null || _ask != null) return;
        _greet = GreetingKind.Login;
        _greetT = 0f;
    }

    private float _dt = 0.008f;
    private long _lastFrameAt;
    private void Frame()
    {
        DrainPosted();

        long frameNow = Environment.TickCount64;
        _dt = _lastFrameAt == 0 ? 0.008f : Math.Clamp((frameNow - _lastFrameAt) / 1000f, 0.001f, 0.05f);
        _lastFrameAt = frameNow;
        AdaptFrameRate();
        EaseRings();
        CheckAlerts();
        var notifStart = _notif;
        var fg = Win32.GetForegroundWindow();
        DetectAgentCancel(fg);
        DetectLanguageChange(fg);

        bool fullscreen = !Pinned(_pinned) && _notch.IsFullscreen(fg);
        var active = fullscreen ? [] : ActiveIndices();

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
            _notch.SetVisible(true);
            _lastFg = IntPtr.Zero;
            Apply(_progress);
        }

        if (visibility.ReturnEarly)
            return;

        bool wasEmpty = _empty;
        _empty = active.Length == 0;

        if (!_empty && _drop < 0f && Array.IndexOf(active, _primary) < 0)
        {
            _primary = active[0];
            _agentNotices.SetPrimary(_primary);
        }

        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _widgets.Length; i++)
        {
            bool desktopBacked = _widgets[i] is CodexWidget codex && codex.IsDesktop;
            _agentNotices.Observe(i, _widgets[i].AgentNotice, now, desktopBacked, allowSelection: _drop < 0f);
        }
        _agentNotices.Tick(now, Live, allowSelection: _drop < 0f);
        if (_drop < 0f)
            _primary = _agentNotices.Primary;
        if (!_empty && Array.IndexOf(active, _primary) < 0)
        {
            _primary = active[0];
            _agentNotices.SetPrimary(_primary);
        }

        if (_userPicked >= 0 && Array.IndexOf(active, _userPicked) < 0) _userPicked = -1;

        if (_drop < 0f && !_empty && _userPicked < 0 && _widgets[_primary].AgentNotice.State != "working")
            foreach (var i in active)
                if (_widgets[i] is ClaudeCodeWidget && _widgets[i].AgentNotice.State == "working")
                {
                    _primary = i;
                    _agentNotices.SetPrimary(i);
                    break;
                }

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

        if (_drop < 0f && !_empty && _userPicked < 0 && !notice)
            for (int i = 0; i < _widgets.Length; i++)
                if (_widgets[i] is DownloadWidget && Live(i))
                { _primary = i; _agentNotices.SetPrimary(i); break; }

        if (_drop < 0f && _btWidget.IsActive && _settings.Enabled(Halo.Settings.FeatureId.Bluetooth))
            for (int i = 0; i < _widgets.Length; i++)
                if (_widgets[i] is BtWidget) { _primary = i; _agentNotices.SetPrimary(i); break; }

        if (_prevDragActive && !FileTray.DragActive) _trayShowUntil = Environment.TickCount64 + 2500;
        _prevDragActive = FileTray.DragActive;
        if (_drop < 0f && (FileTray.DragActive || Environment.TickCount64 < _trayShowUntil)
            && _settings.Enabled(Halo.Settings.FeatureId.FileTray))
            for (int i = 0; i < _widgets.Length; i++)
                if (_widgets[i] is FileTray)
                { _primary = i; _agentNotices.SetPrimary(i); break; }

        for (int i = 0; i < _widgets.Length; i++)
        {
            bool isAct = Live(i);
            if (isAct && !_prevActive[i] && !fullscreen && _drop < 0f)
            {
                if (i == _primary) _arrive = 0f;
                else if (_progress < 0.1f)
                {
                    _pending = _primary;
                    _dropOut = true;
                    _dropIcon = _widgets[i].Icon;
                    _dropImage = _widgets[i].IconImage;
                    _dropCX = _dropCY = LayeredNotch.CircleD / 2f;
                    _drop = 0f;
                }
            }
            _prevActive[i] = isAct;
        }

        Win32.GetCursorPos(out var p);

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

        if (_askTyped != null || _asks.Pending != null || _greet != GreetingKind.None
            || !_settings.Enabled(Halo.Settings.FeatureId.Notifications))
        { while (_notifSrc.Dequeue() is not null) { } }
        else if (_notif == null && !_notifClosing && _progress <= 0.02f && _drop < 0f
            && _notifSrc.Dequeue() is { } item)
        {
            _notif = item;
            _notifDetailOn = false;
            _notifDetail = 0f;
            _notifDetailH = NotifBanner.DetailHeight(item);
            _notifDeadline = DateTime.UtcNow.AddSeconds(item.Duration);
        }

        if (_notif != null && _asks.Pending != null && !_notifDetailOn) _notifClosing = true;
        float prevAskT = _askT;
        int prevAskHover = _askHover;
        var pendingAsk = _notif == null && _settings.Current.Bool("claude.ask", true) ? _asks.Pending : null;
        if (pendingAsk?.Nonce != _ask?.Nonce)
        {
            EndTyping();
            _ask = pendingAsk;
            _askHover = -1;

            if (_ask != null && _askDraftNonce == _ask.Nonce && _askDraft.Length > 0) BeginTyping();
        }

        if (_ask != null)
        {
            _askChips = AskBanner.Chips(_ask, AskBanner.W);
            _askH = AskBanner.Height(_ask, AskBanner.W);

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
            if (overNotif && !_notifDetailOn && _notif.Kind != "language")
                _notifDeadline = Max(_notifDeadline, DateTime.UtcNow.AddSeconds(2.5));
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

        bool open = (hovered || notice || FileTray.DragActive || _apiHold)
            && !_empty && _notif == null && !_moving;

        int dir = open ? 1 : -1;

        float step = _dt / ((open ? OpenSeconds : CloseSeconds) * MotionScale);

        float next = open && FileTray.DragActive ? 1f : Math.Clamp(_progress + dir * step, 0f, 1f);

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

        float dnext = _drop;
        if (_drop >= 0f)
        {
            dnext = _drop + _dt / 0.34f;
            if (dnext >= 1f)
            {
                if (!_dropOut) { _primary = _pending; _agentNotices.SetPrimary(_primary); _arrive = 0f; _userPicked = _pending; }
                _dropOut = false;
                dnext = -1f;
            }
        }

        float anext = _arrive;
        if (_arrive >= 0f) { anext = _arrive + _dt / 0.22f; if (anext >= 1f) anext = -1f; }

        float prevMenu = _menu, prevDrop = _drop, prevArrive = _arrive, prevRowOpen = _rowOpen;
        _menu = mnext;
        _rowOpen = rnext;
        _drop = dnext;
        _arrive = anext;
        PollClick(p);
        HandleTrayInteraction(p, down);

        bool startExpand = _progress <= 0.02f && next > 0.02f;
        bool deskChanged = false;
        if (_askTyped == null && (fg != _lastFg || startExpand))
        {

            bool follow = _settings.Current.Bool(Halo.Settings.SettingsKeys.FollowFocus, true);
            if (follow && fg != _lastFg && _drop < 0f && !_agentNotices.IsOpen(now))
                FollowForeground(fg);
            if (follow && fg != _lastFg) FollowForegroundMedia(ProcessNameOf(fg));
            _lastFg = fg;
            bool desk = _notch.ProbeBehind(out _behind);
            deskChanged = desk != _lastDesktop;
            _lastDesktop = desk;
            if (deskChanged && !desk) _lastCaptureAt = 0;
        }

        int captureEveryMs = _progress > 0.5f ? CaptureOpenMs : CaptureCollapsedMs;
        if (_heavy) captureEveryMs *= 3;

        if (_progress <= 0.5f) captureEveryMs *= Math.Clamp(1 + _notch.StaleStreak / 6, 1, 4);
        if (!_lastDesktop && _behind != IntPtr.Zero && frameNow - _lastCaptureAt >= captureEveryMs)
        {
            _lastCaptureAt = frameNow;
            _notch.CaptureFrom(_behind);
        }
        int cv = _notch.CaptureVersion;
        bool refreshed = cv != _lastCaptureVer;
        _lastCaptureVer = cv;

        bool tick = DateTime.Now.Second != _lastSec;
        _lastSec = DateTime.Now.Second;

        bool forceAnim = false;
        bool animating = _widgets[_primary].Animating;
        if (animating && _progress >= 0.5f) forceAnim = true;
        else if (animating && ++_animTick >= 4) { _animTick = 0; forceAnim = true; }

        bool overNow = _notif != null ? overNotif : hovered && next > 0.98f;
        var mouse = _notif != null
            ? new PointF((p.X - NotifLeft()) / S, (p.Y - _ct) / S)
            : new PointF((p.X - _el) / S, (p.Y - _et) / S);
        bool mouseMoved = WidgetInput.Over != overNow || (overNow && WidgetInput.Mouse != mouse);
        WidgetInput.Over = overNow;
        WidgetInput.Mouse = mouse;

        WidgetInput.Down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;

        float prevStrip = _stripT;
        _stripT = Math.Clamp(_stripT + (AltIndices().Length >= 1 ? 1 : -1) * _dt / 0.22f, 0f, 1f);

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

            || _carryDY != _drawnCarryDY || _dragRow != _drawnDragRow

            || _dragHeld >= DragHold;
        _progress = next;
        _widgetVersion = wv;

        _drawnTyped = _askTyped;
        _drawnCarryDY = _carryDY;
        _drawnDragRow = _dragRow;
        if (changed) Apply(_progress);
    }

    private bool InChip(Win32.POINT p, RectangleF r)
        => p.X >= NotifLeft() + r.X * S && p.X < NotifLeft() + r.Right * S
        && p.Y >= _ct + r.Y * S && p.Y < _ct + r.Bottom * S;

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

    private Color? RingOf(int i)
        => _widgets[i].Ring is { } target ? (_ringShown.TryGetValue(i, out var c) ? c : target) : null;

    private Color? GroupRing(int[] gr)
    {
        Color? first = null;
        foreach (var i in gr)
        {
            if (RingOf(i) is not { } rc) continue;
            first ??= rc;
            if (rc.R != rc.G || rc.G != rc.B) return rc;
        }
        return first;
    }

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

        _stripKinds = _stripOrder.Apply(order);
        return _stripKinds.ConvertAll(k => byKind[k].ToArray());
    }

    private readonly Halo.Settings.SettingsStore _settings;

    private bool Live(int i)
    {
        var feature = FeatureOf(_widgets[i]);
        return _widgets[i].IsActive && (feature is null || _settings.Enabled(feature.Value));
    }

    private static Halo.Settings.FeatureId? FeatureOf(IWidget widget) => widget switch
    {
        MediaWidget or VlcWidget => Halo.Settings.FeatureId.Media,
        DownloadWidget => Halo.Settings.FeatureId.Downloads,
        FileTray => Halo.Settings.FeatureId.FileTray,
        Widgets.BtWidget => Halo.Settings.FeatureId.Bluetooth,
        ClaudeCodeWidget => Halo.Settings.FeatureId.ClaudeCode,
        CodexWidget => Halo.Settings.FeatureId.Codex,
        GenericAgentWidget => Halo.Settings.FeatureId.GenericAgents,
        _ => null,
    };

    private int[] ActiveIndices()
    {
        var active = new List<int>(_widgets.Length);
        for (int i = 0; i < _widgets.Length; i++)
            if (Live(i))
                active.Add(i);
        return [.. active];
    }

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
        int v = Privacy.Version;
        v += _settings.Version;
        foreach (var wgt in _widgets) v += wgt.Version;
        return v;
    }

    private const float DragHold = 0.26f;

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

            _carryWant = (p.Y - _dragFromY) / S;
            _carryDY = Lerp(_carryDY, _carryWant, Math.Clamp(_dt / 0.045f, 0f, 1f));

            int steps = (int)((p.Y - _dragFromY) / D);
            if (steps != 0 && _dragRow < _stripKinds.Count)
            {
                string kind = _stripKinds[_dragRow];
                if (_stripOrder.Move(_stripKinds, kind, steps))
                {
                    _stripOrder.Save(StripOrderPath);
                    _dragRow = Math.Clamp(_dragRow + steps, 0, _stripKinds.Count - 1);
                    _dragFromY += steps * D;

                    _carryWant = (p.Y - _dragFromY) / S;
                    _carryDY -= steps * LayeredNotch.CircleD;
                }
            }
            return true;
        }

        bool wasTap = _dragHeld < DragHold && Math.Abs(p.Y - _dragFromY) < D / 2;
        int row = _dragRow;
        _dragRow = -1;
        _dragHeld = 0f;
        _carryDY = 0f;
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
        int rel = (p.X - mx) / D;
        int pick = rel <= 0 || grp.Length == 1 ? 0 : Math.Clamp(rel - 1, 0, grp.Length - 1);
        _pending = grp[pick];
        _dropIcon = _widgets[_pending].Icon;
        _dropImage = _widgets[_pending].IconImage;
        int DL = LayeredNotch.CircleD;
        _dropCX = rel <= 0 ? DL / 2f : (rel + 0.5f) * DL;
        _dropCY = (row + 0.5f) * DL;
        _drop = 0f;
        _menu = 0f;
        _rowOpen = 0f;
        _row = -1;
    }

    private void PollClick(Win32.POINT p)
    {
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (_moving) { _lastMouseDown = down; return; }
        if (UpdatePinGesture(p, down)) { _lastMouseDown = down; return; }
        if (UpdateStripGesture(p, down)) { _lastMouseDown = down; return; }

        if (down && !_lastMouseDown && !_resizing && _notif == null && _ask is { } ask && _askT > 0.5f)
        {
            bool hitRow = false;
            for (int i = 0; i < _askChips.Count; i++)
                if (InChip(p, _askChips[i].Rect))
                {
                    hitRow = true;

                    if (AskBanner.IsOther(_askChips[i].Option)) BeginTyping();

                    else if (_asks.Answer(ask, _askChips[i].Option.Label))
                    {
                        EndTyping();
                        ClearDraft();
                        _ask = null;
                        _askHover = -1;
                    }
                    break;
                }

            if (!hitRow && _askTyped != null && !InRect(p, NotifLeft(), _ct, Sc(_curW), Sc(_curH)))
                EndTyping();
            _lastMouseDown = down;
            return;
        }
        if (down && !_lastMouseDown && !_resizing && _notif != null)
        {

            var copyR = NotifBanner.CopyRect(_notif, _curW);
            if (!InRect(p, NotifLeft(), _ct, Sc(_curW), Sc(_curH)))
                _notifClosing = true;

            else if (!copyR.IsEmpty
                && p.X >= NotifLeft() + copyR.X * S && p.X < NotifLeft() + copyR.Right * S
                && p.Y >= _ct + copyR.Y * S && p.Y < _ct + copyR.Bottom * S)
            {
                Halo.Interop.Clipboard.SetText(_notif.Code);
                _notif.Copied = true;
                _notifDeadline = Max(_notifDeadline, DateTime.UtcNow.AddSeconds(2));
            }

            else if (!_notifDetailOn && NotifBanner.BodyOverflows(_notif) && p.Y >= _ct + Sc(_curH - 22))
            {
                _notifDetailOn = true;
                _notifDeadline = DateTime.MaxValue;
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

        }
        _lastMouseDown = down;
    }

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

        if (down && !_lastTrayDown)
        {
            _trayPressPath = tray.RowPathAt(ExpandedW, ExpandedH, local);
            _trayPressAt = p;
            _trayMode = 0;
            if (_trayPressPath != null && ctrl) { FileTray.ToggleSelect(_trayPressPath); _trayPressPath = null; _trayMode = -1; }
        }
        else if (down && _trayMode == 0 && _trayPressPath != null)
        {
            int dx = p.X - _trayPressAt.X, dy = p.Y - _trayPressAt.Y;
            if (!inside) StartTrayDragOut();
            else if (dx * dx + dy * dy > 36)
            {
                _trayMode = 1;
                FileTray.BeginReorder(_trayPressPath);
                FileTray.UpdateReorder(tray.RowIndexAt(ExpandedW, ExpandedH, local));
            }
        }
        else if (down && _trayMode == 1)
        {
            if (!inside) { FileTray.CancelReorder(); StartTrayDragOut(); }
            else FileTray.UpdateReorder(tray.RowIndexAt(ExpandedW, ExpandedH, local));
        }

        if (!down && _lastTrayDown)
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

        if (paths.Length > 0 && Halo.Interop.FileDrag.Out(paths) && !CursorOverNotch()) FileTray.RemovePaths(paths);
        _trayPressPath = null; _trayMode = -1;
    }

    private bool CursorOverNotch()
    {
        return Win32.GetCursorPos(out var p) && Win32.GetWindowRect(_notch.Hwnd, out var r)
            && p.X >= r.left && p.X < r.right && p.Y >= r.top && p.Y < r.bottom;
    }

    private static bool InRect(Win32.POINT p, int left, int top, int w, int h)
        => p.X >= left && p.X < left + w && p.Y >= top && p.Y < top + h;

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
        if (_shrink > 0f)
        {
            float s = SmoothStep(_shrink);
            w = (int)Lerp(w, 96, s);
            h = (int)Lerp(h, 12, s);
            r = (int)Lerp(r, 6, s);
        }
        bool glass = !_lastDesktop;

        int cT = (int)((glass ? TintAppCollapsed : TintDeskCollapsed) * GlassScale);
        int eT = (int)((glass ? TintAppExpanded : TintDeskExpanded) * GlassScale);
        int tint = (int)Lerp(cT, eT, t);

        if (_empty && !Privacy.Active)
            tint = (int)Lerp(tint, EmptyCatchAlpha, SmoothStep(_shrink));
        float fade = Math.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
        float mini = Math.Clamp(1f - t / 0.35f, 0f, 1f);

        if (_notif == null && _ask != null && _askT > 0f)
        {
            float ea = EaseOutBack(_askT);
            w = (int)Lerp(w, AskBanner.W, ea);
            h = (int)Lerp(h, _askH, ea);
            r = (int)Lerp(r, 26, ea);
            tint = (int)Lerp(cT, glass ? TintAskApp : TintAskDesk, _askT);
            fade = Math.Clamp((_askT - 0.45f) / 0.55f, 0f, 1f);

            mini *= Math.Clamp(1f - _askT / 0.35f, 0f, 1f);
        }
        if (_notif != null && _notifT > 0f)
        {
            float en = EaseOutBack(_notifT);
            float nh = Lerp(NotifBanner.SummaryH, _notifDetailH, SmoothStep(_notifDetail));
            w = (int)Lerp(w, NotifBanner.W, en);
            h = (int)Lerp(h, nh, en);
            r = (int)Lerp(r, 26, en);
            tint = (int)Lerp(cT, eT, _notifT);
            fade = Math.Clamp((_notifT - 0.45f) / 0.55f, 0f, 1f);
            mini *= Math.Clamp(1f - _notifT / 0.35f, 0f, 1f);
        }
        float arrive = _arrive < 0f ? 1f : 1f - (1f - _arrive) * (1f - _arrive);
        mini *= arrive;

        var groups = _empty ? new List<int[]>() : Groups();

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
            Show = _greet == GreetingKind.None && (groups.Count >= 1 || _stripT > 0.01f),
            Appear = SmoothStep(_stripT),

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
            float pillX = w - h / 2f, pillY = h / 2f;
            (frame.FromX, frame.FromY, frame.ToX, frame.ToY) = _dropOut
                ? (pillX, pillY, circleX, circleY)
                : (circleX, circleY, pillX, pillY);
        }

        if (_greet != GreetingKind.None)
        {
            var gf = _greet == GreetingKind.Install
                ? GreetingPlan.Install(_greetT) : GreetingPlan.Login(_greetT);
            w = (int)gf.PillW;
            h = (int)gf.PillH;
            r = (int)gf.Radius;
            fade = 1f;
            mini = 0f;

            _drop = -1f;
            _arrive = -1f;
            _stripT = 0f;
        }

        Action<Graphics, int, int, float> content = _greet != GreetingKind.None
            ? (g, cw, ch, f) => DrawGreeting(g, cw, ch)
            : _notif == null && _ask is { } q && _askT > 0f
            ? (g, cw, ch, f) => AskBanner.Draw(g, cw, ch, f, q, _askHover, tint, _askTyped)
            : _notif is { } toast && _notifT > 0f
            ? (g, cw, ch, f) => NotifBanner.Draw(g, cw, ch, f, toast, SmoothStep(_notifDetail), _notifDetailOn)
            : _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawContent;

        bool pin = _notif == null && _ask == null && _greet == GreetingKind.None && !TrayFront;
        _curW = w;
        _curH = h;
        _notch.OffsetX = _offsetX;
        float holdCue = _moving ? 0f : _holdT;

        bool banner = _notif != null || (_ask != null && _askT > 0f);
        float glassFade = _empty && !Privacy.Active && !banner ? 1f - SmoothStep(_shrink) : 1f;
        _notch.Render(w, h, r, tint, fade, mini, glass, frame,
            (g, cw, ch, f) => { content(g, cw, ch, f); if (pin) DrawPin(g, cw, ch, f); if (holdCue > 0.01f) DrawHoldCue(g, cw, ch); },
            _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawCollapsed,
            glassFade, banner ? BannerClarity : 0f);
    }

    private void DrawGreeting(Graphics g, int w, int h)
    {
        var f = _greet == GreetingKind.Install
            ? GreetingPlan.Install(_greetT) : GreetingPlan.Login(_greetT);
        var box = Greeting.InkBox(w, h);

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

    private void EndTyping()
    {
        if (_askTyped == null && !_keys.Active) return;
        if (_askTyped != null) { _askDraft = _askTyped; _askDraftNonce = _ask?.Nonce; }
        _askTyped = null;
        _keys.Stop();
    }

    private void ClearDraft()
    {
        _askDraft = "";
        _askDraftNonce = null;
    }

    private void TypedChar(char c)
    {
        if (_askTyped == null) return;
        if (c < ' ' || c == 0x7F) return;
        if (_askTyped.Length >= 400) return;
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

            try
            {
                if (Clipboard.Text() is { Length: > 0 } t)
                    _askTyped = (_askTyped + t.Replace('\r', ' ').Replace('\n', ' ')).Trim();
            }
            catch { }
        }
    }

    private void FollowForeground(IntPtr fg)
    {
        try
        {
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return;
            for (int i = 0; i < _widgets.Length; i++)
            {
                if (i == _primary || !Live(i)) continue;
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

    private void FollowForegroundMedia(string fgProc)
    {
        if (string.IsNullOrEmpty(fgProc)) return;
        if (_widgets[_primary] is not MediaWidget pm || !pm.IsActive || !AppMatches(pm.App, fgProc)) return;
        for (int i = 0; i < _widgets.Length; i++)
            if (i != _primary && _widgets[i] is MediaWidget m && m.IsActive)
            { _primary = i; _agentNotices.SetPrimary(i); return; }
    }

    private static bool AppMatches(string app, string proc)
    {
        proc = proc.ToLowerInvariant();
        return app.Length > 1 && proc.Length > 1 && (app == proc || app.Contains(proc) || proc.Contains(app));
    }

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

    private static RectangleF PinRect(int w, int h) => new(9, 4, 24, 24);

    private bool OverPin(Win32.POINT p)
    {
        var r = PinRect(ExpandedW, ExpandedH);
        return p.X >= _el + r.X * S && p.X < _el + (r.X + r.Width) * S
            && p.Y >= _et + r.Y * S && p.Y < _et + (r.Y + r.Height) * S;
    }

    private DateTime _pinPressAt = DateTime.MaxValue;
    private bool _pinHoldFired;
    private const double PinHoldSeconds = 0.55;

    private bool UpdatePinGesture(Win32.POINT p, bool down)
    {

        bool over = _progress > 0.9f && _notif == null && !TrayFront && OverPin(p);
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

        if (!_pinHoldFired && over) { _pinned = !_pinned; SavePin(); }
        _pinPressAt = DateTime.MaxValue;
        return true;
    }

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
        if (hv > 0.02f)
        {
            using var f = new Font("Segoe UI", 11f, GraphicsUnit.Pixel);

            string label = _pinned ? "unpin" : "pin on top";

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

    private static readonly Color Slate = Color.FromArgb(255, 154, 165, 180);

    private static void Sphere(Graphics g, RectangleF head, float hr, GraphicsPath? needle, float a,
        Color baseColor, Color? needleColor = null)
    {
        int A(float f) => (int)Math.Clamp(f * a, 0, 255);
        Color Tint(Color c, float k) => Color.FromArgb(A(255),
            (int)Math.Clamp(c.R * k, 0, 255),
            (int)Math.Clamp(c.G * k, 0, 255),
            (int)Math.Clamp(c.B * k, 0, 255));
        Color Shade(float k) => Tint(baseColor, k);

        if (needle != null)
        {
            var nc = needleColor ?? baseColor;
            using var nb = new LinearGradientBrush(
                new PointF(-3f * hr, 0), new PointF(3f * hr, 0), Tint(nc, 1.16f), Tint(nc, 0.52f));
            g.FillPath(nb, needle);
        }

        using (var shadow = new GraphicsPath())
        {
            shadow.AddEllipse(head.X + hr * 0.16f, head.Y + hr * 0.42f, head.Width * 0.92f, head.Height * 0.92f);
            using var sb = new PathGradientBrush(shadow)
            {
                CenterColor = Color.FromArgb(A(96), 0, 0, 0),
                SurroundColors = [Color.FromArgb(0, 0, 0, 0)],
            };
            g.FillPath(sb, shadow);
        }

        using (var hp = new GraphicsPath())
        {
            hp.AddEllipse(head);
            using var pgb = new PathGradientBrush(hp)
            {

                CenterPoint = new PointF(head.X + hr * 0.60f, head.Y + hr * 0.58f),
                CenterColor = Shade(1.34f),
                SurroundColors = [Shade(0.55f)],
            };
            g.FillPath(pgb, hp);
        }

        using (var spec = new GraphicsPath())
        {
            spec.AddEllipse(head.X + hr * 0.34f, head.Y + hr * 0.30f, hr * 0.62f, hr * 0.62f);
            using var sb = new PathGradientBrush(spec)
            {
                CenterColor = Color.FromArgb(A(215), 255, 255, 255),
                SurroundColors = [Color.FromArgb(0, 255, 255, 255)],
            };
            g.FillPath(sb, spec);
        }

        using (var rim = new Pen(Color.FromArgb(A(70), 255, 255, 255), Math.Max(0.7f, hr * 0.11f)))
            g.DrawArc(rim, head.X + 0.6f, head.Y + 0.6f, head.Width - 1.2f, head.Height - 1.2f, 20f, 130f);
    }

    internal static void DrawPushpin(Graphics g, RectangleF r, bool pinned, float hover, float a,
        bool recordable = false, float holdT = 0f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var st = g.Save();
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 24f * 0.7f;
        g.TranslateTransform(cx, cy);
        g.RotateTransform(28f);
        float hr = 6.4f * u;
        var head = new RectangleF(-hr, -3f * u - hr, hr * 2, hr * 2);
        using var needle = new GraphicsPath();
        needle.AddPolygon(new[] { new PointF(-2.3f * u, 2.5f * u), new PointF(2.3f * u, 2.5f * u), new PointF(0, 12f * u) });

        float grow = 1f + 0.18f * holdT;
        if (grow > 1.001f)
        {
            float gh = hr * grow;
            head = new RectangleF(-gh, -3f * u - gh, gh * 2, gh * 2);
            hr = gh;
        }

        if (recordable)
        {

            var amber = Color.FromArgb(255, 255, 200, 92);
            if (pinned)
            {

                Sphere(g, head, hr, needle, a, amber, Slate);
            }
            else
            {
                using (var pen = new Pen(Color.FromArgb((int)((122 + 78 * hover) * a), 255, 255, 255), 1.7f * u)
                       { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawPath(pen, needle);
                Sphere(g, head, hr, null, a, amber);
            }
        }
        else if (pinned)
        {
            Sphere(g, head, hr, needle, a, Slate);
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

    private void DetectAgentCancel(IntPtr fg)
    {
        if ((Win32.GetAsyncKeyState(Win32.VK_ESCAPE) & 0x8000) == 0) return;
        if (!ForegroundIsAgentHost(fg)) return;
        if (_claudeStore.Current?.State == "compacting")
            ClaudeCodeWidget.MarkCompactCancelled(_claudeStore.Current?.StartedAt);
        if (_codexStore.Current?.State == "compacting")
            CodexWidget.MarkCompactCancelled(_codexStore.Current?.StartedAt);

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

        CodexWidget.MarkTurnCancelled(snapshot.StartedAt);
    }

    private void OnClipboardImage(Bitmap shot, bool isScreenshot)
    {
        if (!Alert("clipboard")) return;
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
            Preview = shot,
            LaunchPath = path,

            Icon = isScreenshot ? Badges.Shot() : Badges.Clip(),
        });
    }

    private void DetectLanguageChange(IntPtr fg)
    {
        try
        {
            uint tid = Win32.GetWindowThreadProcessId(fg, out _);
            if (tid == 0) return;
            uint lang = (uint)(Win32.GetKeyboardLayout(tid).ToInt64() & 0xFFFF);
            if (lang == 0) return;
            long now = Environment.TickCount64;
            if (fg != _langFg)
            {
                _langFg = fg; _lastLangId = lang; _langFgSince = now;
                return;
            }

            if (_lastLangId != 0 && lang != _lastLangId && now - _langFgSince > 600 && Alert("language"))
                ShowLanguageNotif(lang);
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
            var lang = ci.Parent.EnglishName.Length > 0 ? ci.Parent.EnglishName : ci.EnglishName;
            if (lang.Length > 0) name = lang;
            code = ci.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch { }
        var item = new Halo.Notifications.NotifItem
        {
            App = "Keyboard", Title = name, Icon = Badges.Language(code),
            Kind = "language", Duration = 1,
        };

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

        new Halo.Notifications.NotifItem
        {
            App = "Tehran",
            Title = Almanac.Headline(DateTime.Today.AddHours(1), new Almanac.Weather(27, 0, Day: false), metric: true),
            Body = Almanac.Detail(DateTime.Today.AddHours(1), CalendarKind.SolarHijri),
            Icon = Badges.Local(0xE708, 232, 32f),
        },
    };

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

        try
        {
            string legacy = System.IO.File.Exists(PinPath) && System.IO.File.ReadAllText(PinPath).Trim() == "1"
                ? "on" : "off";
            _pinned = _settings.Current.Bool(Halo.Settings.SettingsKeys.OverFullscreen, legacy == "on");
        }
        catch { }
    }

    private DateTime _offsetStamp;

    private void ReloadOffset()
    {
        try
        {
            var stamp = System.IO.File.GetLastWriteTimeUtc(OffsetPath);
            if (stamp == _offsetStamp) return;
            _offsetStamp = stamp;
            if (float.TryParse(System.IO.File.ReadAllText(OffsetPath),
                    System.Globalization.CultureInfo.InvariantCulture, out var v) && v != _offsetX)
                _offsetX = v;
        }
        catch { }
    }

    private void SaveOffset()
    {
        try
        {
            System.IO.File.WriteAllText(OffsetPath, _offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _offsetStamp = System.IO.File.GetLastWriteTimeUtc(OffsetPath);
        }
        catch { }
    }

    private void SavePin()
    {
        try { System.IO.File.WriteAllText(PinPath, _pinned ? "1" : "0"); } catch { }
        try { _settings.Set(Halo.Settings.SettingsKeys.OverFullscreen, _pinned ? "on" : "off"); } catch { }
    }

    private static readonly string RecordablePath = System.IO.Path.Combine(HaloDir, "capturable");
    private bool _recordable;

    private void LoadRecordable()
    {
        try
        {
            bool legacy = System.IO.File.Exists(RecordablePath)
                && System.IO.File.ReadAllText(RecordablePath).Trim() == "1";
            _recordable = _settings.Current.Bool(Halo.Settings.SettingsKeys.InCaptures, legacy);
        }
        catch { }
    }

    private void SaveRecordable()
    {
        try { System.IO.File.WriteAllText(RecordablePath, _recordable ? "1" : "0"); } catch { }
        try { _settings.Set(Halo.Settings.SettingsKeys.InCaptures, _recordable ? "on" : "off"); } catch { }
    }

    private bool PressOnControl(Win32.POINT p)
    {
        if (_progress <= 0.9f || _primary < 0 || _primary >= _widgets.Length) return false;

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
        const float snap = 55f;
        if (_moving)
        {
            if (down)
            {
                float raw = Math.Clamp(p.X - _moveGrabDX - centre,
                    -(_notch.WorkWidth / 2f - Sc(CollapsedW) / 2f - 8), _notch.WorkWidth / 2f - Sc(CollapsedW) / 2f - 8);
                _offsetX = MathF.Abs(raw) < snap ? 0f : raw;
            }
            else { if (MathF.Abs(_offsetX) < snap) _offsetX = 0f; _moving = false; _holdT = 0f; SaveOffset(); }
            return;
        }

        bool holding = down && hovered && !_resizing && _notif == null
                    && !FileTray.DragActive && _trayPressPath == null && _trayMode < 1
                    && !PressOnControl(p);
        bool still = Math.Abs(p.X - _holdAnchor.X) <= 8 && Math.Abs(p.Y - _holdAnchor.Y) <= 8;
        if (holding && _holdStart != DateTime.MaxValue && still)
        {
            _holdT = Math.Clamp((float)((DateTime.UtcNow - _holdStart).TotalSeconds / HoldSeconds), 0f, 1f);
            if (_holdT >= 1f) { _moving = true; _moveGrabDX = p.X - (int)(centre + _offsetX); _holdStart = DateTime.MaxValue; }
        }
        else if (holding) { _holdStart = DateTime.UtcNow; _holdAnchor = p; _holdT = 0f; }
        else { _holdStart = DateTime.MaxValue; _holdT = 0f; }
    }

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
