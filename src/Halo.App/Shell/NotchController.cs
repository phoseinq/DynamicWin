using System;
using System.Drawing;
using Halo.ClaudeCode;
using Halo.Interop;
using Halo.Widgets;
using Windows.System;

namespace Halo.Shell;

internal sealed class NotchController
{
    private const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    private const int ExpandedW = 560, ExpandedH = 220, ExpandedR = 30;
    private const int TintDeskCollapsed = 255, TintDeskExpanded = 245;
    private const int TintAppCollapsed = 120, TintAppExpanded = 60;
    private const float OpenSeconds = 0.16f, CloseSeconds = 0.24f; // open snappier than close
    private const int CaptureFast = 2, CaptureSlow = 12; // glass capture cadence: ~60fps expanded, ~10fps collapsed

    private readonly LayeredNotch _notch;
    private readonly StatusStore _store;
    private readonly IWidget[] _widgets;
    private readonly DispatcherQueueTimer _timer;
    private readonly int _cl, _ct, _el, _et;

    private int _primary;
    private float _progress;
    private float _menu;        // circle → dropdown open, 0..1
    private float _drop = -1f;  // <0 idle, else 0..1 "drop into pill" animation
    private float _arrive = -1f; // <0 idle, else 0..1 new-app "opening" bloom after a swap
    private int _pending, _dropSlot;
    private string _dropIcon = "";
    private Bitmap? _dropImage;
    private int _widgetVersion = -1;
    private int _lastSec = -1;
    private bool _lastMouseDown;
    private bool _hidden;
    private bool _lastDesktop = true;
    private IntPtr _lastFg = IntPtr.Zero;
    private IntPtr _behind = IntPtr.Zero;
    private int _captureTick;
    private int _animTick;
    private int _lastCaptureVer;
    private DateTime _noticeUntil;       // Apple-style: pill auto-expands while Claude waits for input
    private string? _lastCcState;
    private int _noticeRestore = -1;

    public NotchController(LayeredNotch notch)
    {
        _notch = notch;
        _store = new StatusStore();
        _widgets = new IWidget[] { new ClaudeCodeWidget(_store, Cancel), new MediaWidget() };

        _cl = notch.WorkLeft + (notch.WorkWidth - CollapsedW) / 2;
        _ct = notch.WorkTop;
        _el = notch.WorkLeft + (notch.WorkWidth - ExpandedW) / 2;
        _et = notch.WorkTop;

        Apply(0f);

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
        if (_notch.IsFullscreen(fg))
        {
            if (!_hidden) { _hidden = true; _notch.SetVisible(false); }
            return;
        }
        if (_hidden) { _hidden = false; _notch.SetVisible(true); _lastFg = IntPtr.Zero; }

        // primary must be an active widget; fall back to the first active one if it went inactive
        if (_drop < 0f && !_widgets[_primary].IsActive)
        {
            var act = ActiveIndices();
            if (act.Length > 0) _primary = act[0];
        }

        // Claude asked something, or a compact just finished → notification: expand, hold, collapse
        var ccState = _store.Current?.State;
        bool asked = ccState == "waiting_input" && _lastCcState != "waiting_input";
        bool compacted = _lastCcState == "compacting" && ccState != null && ccState != "compacting";
        if (asked || compacted)
        {
            _noticeUntil = DateTime.UtcNow.AddSeconds(compacted ? 4 : 6);
            if (_widgets[_primary] is not ClaudeCodeWidget && _drop < 0f)
            { _noticeRestore = _primary; _primary = 0; }
        }
        _lastCcState = ccState;
        bool notice = ccState == "waiting_input" && DateTime.UtcNow < _noticeUntil;
        if (!notice && _noticeRestore >= 0 && _progress < 0.02f)
        {
            if (_widgets[_noticeRestore].IsActive) _primary = _noticeRestore;
            _noticeRestore = -1;
        }

        Win32.GetCursorPos(out var p);
        bool hovered = _progress > 0.02f
            ? InRect(p, _el, _et, ExpandedW, ExpandedH)
            : InRect(p, _cl, _ct, CollapsedW, CollapsedH);
        bool open = hovered || notice;

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
            if (dnext >= 1f) { _primary = _pending; dnext = -1f; _arrive = 0f; }
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
        bool changed = next != _progress || wv != _widgetVersion || deskChanged
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
        int n = 0;
        for (int i = 0; i < _widgets.Length; i++) if (_widgets[i].IsActive) n++;
        var r = new int[n];
        int j = 0;
        for (int i = 0; i < _widgets.Length; i++) if (_widgets[i].IsActive) r[j++] = i;
        return r;
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

        var alts = AltIndices();
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
        if (frame.Dropping)
        {
            frame.FromX = w + LayeredNotch.CircleGap + LayeredNotch.CircleD / 2f;
            frame.FromY = LayeredNotch.CircleY + _dropSlot * LayeredNotch.CircleD + LayeredNotch.CircleD / 2f;
            frame.ToX = w - h / 2f; // fuse into the pill's rounded end (metaball dominates), not fly to centre
            frame.ToY = h / 2f;
        }
        _notch.Render(w, h, r, tint, fade, mini, glass, frame,
            _widgets[_primary].DrawContent, _widgets[_primary].DrawCollapsed);
    }

    private void Cancel()
    {
        var pid = _store.Current?.Pid ?? 0;
        if (pid > 0) CcCancel.Request(pid);
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
