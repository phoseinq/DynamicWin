using System;
using System.Collections.Generic;
using System.Drawing;
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

        bool waiting = notice.State == "waiting_input" && previous.State != "waiting_input";
        bool started = notice.State == "working" && previous.State != "working";
        // only a fresh compactedAt counts — merely leaving "compacting" could be a cancelled compact
        bool compacted = notice.CompactedAt is { } doneAt && doneAt != previous.CompactedAt &&
            now - doneAt < TimeSpan.FromSeconds(30);
        if (waiting || compacted)
            _pending[widgetIndex] = new NoticeWindow(now.AddSeconds(waiting ? 6 : 4), desktopBacked, _nextOrder++);

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
    private const float OpenSeconds = 0.16f, CloseSeconds = 0.24f; // open snappier than close
    private const int CaptureFast = 2, CaptureSlow = 12; // glass capture cadence: ~60fps expanded, ~10fps collapsed

    private readonly LayeredNotch _notch;
    private readonly StatusStore _claudeStore;
    private readonly CodexStatusStore _codexStore;
    private readonly CodexDesktopRuntime _codexDesktopRuntime;
    private readonly IWidget[] _widgets;
    private readonly AgentNoticeCoordinator _agentNotices;
    private readonly DispatcherQueueTimer _timer;
    private readonly int _cl, _ct, _el, _et;

    private int _primary;
    private float _progress;
    private float _menu;        // circle → dropdown open, 0..1
    private float _drop = -1f;  // <0 idle, else 0..1 "drop into pill" animation
    private float _arrive = -1f; // <0 idle, else 0..1 new-app "opening" bloom after a swap
    private int _pending, _dropSlot;
    private bool _dropOut;      // drop runs pill → circle (new-app arrival toss)
    private string _dropIcon = "";
    private Bitmap? _dropImage;
    private readonly bool[] _prevActive;
    private int _widgetVersion = -1;
    private int _lastSec = -1;
    private bool _lastMouseDown;
    private bool _hiddenForFullscreen;
    private bool _empty; // no active widgets: pill stays visible but renders blank
    private bool _lastDesktop = true;
    private IntPtr _lastFg = IntPtr.Zero;
    private IntPtr _behind = IntPtr.Zero;
    private int _captureTick;
    private int _animTick;
    private int _lastCaptureVer;
    public NotchController(LayeredNotch notch)
    {
        _notch = notch;
        _claudeStore = new StatusStore();
        _codexStore = new CodexStatusStore();
        _codexDesktopRuntime = CodexDesktopRuntime.Shared;
        CodexLimits.Attach(_codexStore);
        CodexLimits.UpdateFrom(_codexStore.Current);
        _widgets = [
            new MediaWidget(),
            new ClaudeCodeWidget(_claudeStore, CancelClaude),
            new CodexWidget(_codexStore, CancelCodex, () => _codexDesktopRuntime.Presence.Running),
        ];

        _cl = notch.WorkLeft + (notch.WorkWidth - CollapsedW) / 2;
        _ct = notch.WorkTop;
        _el = notch.WorkLeft + (notch.WorkWidth - ExpandedW) / 2;
        _et = notch.WorkTop;

        var active = ActiveIndices();
        _empty = active.Length == 0;
        if (!_empty) _primary = active[0];
        _prevActive = new bool[_widgets.Length];
        for (int i = 0; i < _widgets.Length; i++) _prevActive[i] = _widgets[i].IsActive;
        Apply(0f); // empty or not, the pill shows from the first frame (boot = blank pill)
        _agentNotices = new AgentNoticeCoordinator(_primary);

        Dispatcher.Ensure();
        var dq = DispatcherQueue.GetForCurrentThread();
        _timer = dq.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(8);
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var fg = Win32.GetForegroundWindow();
        DetectCompactCancel(fg);
        bool fullscreen = _notch.IsFullscreen(fg);
        var active = fullscreen ? [] : ActiveIndices();
        var visibility = NotchVisibility.Decide(fullscreen, _hiddenForFullscreen);
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
        bool notice = _drop < 0f && _agentNotices.IsOpen(now);

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
                    _dropSlot = Math.Max(0, Array.IndexOf(AltIndices(), i));
                    _drop = 0f;
                }
            }
            _prevActive[i] = isAct;
        }

        Win32.GetCursorPos(out var p);
        bool hovered = _progress > 0.02f
            ? InRect(p, _el, _et, ExpandedW, ExpandedH)
            : InRect(p, _cl, _ct, CollapsedW, CollapsedH);
        bool open = (hovered || notice) && !_empty; // an empty pill has nothing to expand into

        int dir = open ? 1 : -1;
        float step = 0.008f / (open ? OpenSeconds : CloseSeconds);
        float next = Math.Clamp(_progress + dir * step, 0f, 1f);

        // circle dropdown: opens while hovering it, only when the pill is collapsed
        int alt = AltIndices().Length;
        float mnext = _menu;
        if (alt >= 2 && _progress < 0.05f && _drop < 0f && InMenu(p))
            mnext = Math.Min(_menu + step, 1f);
        else
            mnext = Math.Max(_menu - step, 0f);

        // drop-into-pill animation; on landing, kick off the "opening" bloom for the new app
        float dnext = _drop;
        if (_drop >= 0f)
        {
            dnext = _drop + 0.008f / 0.34f; // slower = more liquid
            if (dnext >= 1f)
            {
                if (!_dropOut) { _primary = _pending; _agentNotices.SetPrimary(_primary); _arrive = 0f; }
                _dropOut = false;
                dnext = -1f;
            }
        }

        float anext = _arrive;
        if (_arrive >= 0f) { anext = _arrive + 0.008f / 0.22f; if (anext >= 1f) anext = -1f; }

        // commit menu/drop before PollClick so a click that starts a drop isn't clobbered
        float prevMenu = _menu, prevDrop = _drop, prevArrive = _arrive;
        _menu = mnext;
        _drop = dnext;
        _arrive = anext;
        PollClick(p);

        bool startExpand = _progress <= 0.02f && next > 0.02f;
        bool deskChanged = false;
        if (fg != _lastFg || startExpand)
        {
            _lastFg = fg;
            bool desk = _notch.ProbeBehind(out _behind);
            deskChanged = desk != _lastDesktop;
            _lastDesktop = desk;
            if (deskChanged && !desk) _captureTick = CaptureSlow; // enter app → capture glass this tick
        }

        int captureEvery = _progress > 0.5f ? CaptureFast : CaptureSlow;
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

        // animated preview (e.g. equalizer): force ~30fps re-render while collapsed
        bool forceAnim = false;
        if (_widgets[_primary].Animating && _progress < 0.5f && ++_animTick >= 4) { _animTick = 0; forceAnim = true; }

        // cursor in panel coords for widget hover effects; redraw as it moves over the open panel
        var mouse = new PointF(p.X - _el, p.Y - _et);
        bool mouseMoved = WidgetInput.Over != (hovered && next > 0.98f) || (WidgetInput.Over && WidgetInput.Mouse != mouse);
        WidgetInput.Over = hovered && next > 0.98f;
        WidgetInput.Mouse = mouse;

        int wv = WidgetVersion();
        bool changed = next != _progress || wv != _widgetVersion || deskChanged || wasEmpty != _empty
            || refreshed || tick || _menu != prevMenu || _drop != prevDrop || _arrive != prevArrive || forceAnim || mouseMoved;
        _progress = next;
        _widgetVersion = wv;
        if (changed) Apply(_progress);
    }

    // hover region of the (possibly open) dropdown, in screen coords
    private bool InMenu(Win32.POINT p)
    {
        int alt = AltIndices().Length;
        int x = _cl + CollapsedW + LayeredNotch.CircleGap;
        float open = EaseOutBack(Math.Clamp(_menu, 0f, 1f));
        float hNow = LayeredNotch.CircleD + (alt - 1) * LayeredNotch.CircleD * Math.Max(0f, open);
        return p.X >= x && p.X < x + LayeredNotch.CircleD
            && p.Y >= _ct && p.Y < _ct + Math.Max(LayeredNotch.CircleD, hNow);
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
        int v = 0;
        foreach (var wgt in _widgets) v += wgt.Version;
        return v;
    }

    private void PollClick(Win32.POINT p)
    {
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (down && !_lastMouseDown)
        {
            if (_progress > 0.9f)
            {
                foreach (var (r, onClick) in _widgets[_primary].Buttons(ExpandedW, ExpandedH))
                {
                    int bx = _el + (int)r.X, by = _et + (int)r.Y;
                    if (p.X >= bx && p.X < bx + r.Width && p.Y >= by && p.Y < by + r.Height)
                    {
                        onClick(new PointF(p.X - _el, p.Y - _et));
                        break;
                    }
                }
            }
            else if (_progress < 0.1f && ActiveIndices().Length >= 2 && _drop < 0f && InMenu(p))
            {
                int alt = AltIndices().Length;
                int slot = Math.Clamp((p.Y - _ct) / LayeredNotch.CircleD, 0, alt - 1);
                _pending = AltIndices()[slot];
                _dropIcon = _widgets[_pending].Icon;
                _dropImage = _widgets[_pending].IconImage;
                _dropSlot = slot;
                _drop = 0f;
                _menu = 0f;
            }
        }
        _lastMouseDown = down;
    }

    private static bool InRect(Win32.POINT p, int left, int top, int w, int h)
        => p.X >= left && p.X < left + w && p.Y >= top && p.Y < top + h;

    private void Apply(float t)
    {
        float e = EaseOutBack(t);
        int w = (int)Lerp(CollapsedW, ExpandedW, e);
        int h = (int)Lerp(CollapsedH, ExpandedH, e);
        int r = (int)Lerp(CollapsedR, ExpandedR, e);
        bool glass = !_lastDesktop;
        int cT = glass ? TintAppCollapsed : TintDeskCollapsed;
        int eT = glass ? TintAppExpanded : TintDeskExpanded;
        int tint = (int)Lerp(cT, eT, t);
        float fade = Math.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
        float mini = Math.Clamp(1f - t / 0.35f, 0f, 1f); // collapsed preview: full when collapsed, gone by t=0.35
        float arrive = _arrive < 0f ? 1f : 1f - (1f - _arrive) * (1f - _arrive); // easeOutQuad bloom after swap
        mini *= arrive;

        var alts = _empty ? Array.Empty<int>() : AltIndices();
        var frame = new MenuFrame
        {
            Show = alts.Length >= 1,
            Icons = Array.ConvertAll(alts, i => _widgets[i].Icon),
            Images = Array.ConvertAll(alts, i => _widgets[i].IconImage),
            Open = EaseOutBack(Math.Clamp(_menu, 0f, 1f)),
            Dropping = _drop >= 0f,
            DropIcon = _dropIcon,
            DropImage = _dropImage,
            Drop = _drop >= 0f ? _drop : 0f,
        };
        frame.Outward = _dropOut;
        if (frame.Dropping)
        {
            float circleX = w + LayeredNotch.CircleGap + LayeredNotch.CircleD / 2f;
            float circleY = LayeredNotch.CircleY + _dropSlot * LayeredNotch.CircleD + LayeredNotch.CircleD / 2f;
            float pillX = w - h / 2f, pillY = h / 2f; // pill's rounded end (metaball dominates)
            (frame.FromX, frame.FromY, frame.ToX, frame.ToY) = _dropOut
                ? (pillX, pillY, circleX, circleY)  // arrival: blob detaches from the pill into the circle
                : (circleX, circleY, pillX, pillY); // swap: picked circle fuses into the pill
        }
        // no active widget → bare glass pill (still visible after boot, just blank)
        _notch.Render(w, h, r, tint, fade, mini, glass, frame,
            _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawContent,
            _empty ? static (_, _, _, _) => { } : _widgets[_primary].DrawCollapsed);
    }

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

    private void CancelClaude()
    {
        var pid = _claudeStore.Current?.Pid ?? 0;
        if (pid > 0) CcCancel.Request(pid);
    }

    private void CancelCodex()
    {
        var snapshot = _codexStore.Current;
        if (snapshot is { Source: CodexSurface.Cli, State: "working", ConsolePid: > 0 })
            CcCancel.Request(snapshot.ConsolePid);
        else if (snapshot is { Source: CodexSurface.Desktop, State: "working" })
            _codexDesktopRuntime.TryCancel();
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float EaseOutBack(float t)
    {
        const float c1 = 1.2f;
        const float c3 = c1 + 1f;
        float p = t - 1f;
        return 1f + c3 * MathF.Pow(p, 3f) + c1 * MathF.Pow(p, 2f);
    }
}
