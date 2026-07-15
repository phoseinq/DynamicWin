using System;
using Halo.ClaudeCode;
using Halo.Interop;
using Halo.Widgets;
using Windows.System;

namespace Halo.Shell;

internal sealed class NotchController
{
    private const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    private const int ExpandedW = 560, ExpandedH = 220, ExpandedR = 30;
    private const int TintCollapsed = 235, TintExpanded = 60;
    private const float DurationSeconds = 0.28f;

    private readonly LayeredNotch _notch;
    private readonly StatusStore _store;
    private readonly IWidget _widget;
    private readonly DispatcherQueueTimer _timer;
    private readonly int _cl, _ct, _el, _et;

    private float _progress;
    private int _statusVersion = -1;
    private bool _lastMouseDown;

    public NotchController(LayeredNotch notch)
    {
        _notch = notch;
        _store = new StatusStore();
        _widget = new ClaudeCodeWidget(_store, Cancel);

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
        Win32.GetCursorPos(out var p);
        bool hovered = _progress > 0.02f
            ? InRect(p, _el, _et, ExpandedW, ExpandedH)
            : InRect(p, _cl, _ct, CollapsedW, CollapsedH);

        int dir = hovered ? 1 : -1;
        float step = 0.008f / DurationSeconds;
        float next = Math.Clamp(_progress + dir * step, 0f, 1f);

        PollClick(p);

        if (next != _progress || _store.Version != _statusVersion)
        {
            _progress = next;
            _statusVersion = _store.Version;
            Apply(_progress);
        }
    }

    private void PollClick(Win32.POINT p)
    {
        bool down = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
        if (down && !_lastMouseDown && _progress > 0.9f)
        {
            var r = _widget.ExpandedButton(ExpandedW, ExpandedH);
            int bx = _el + (int)r.X, by = _et + (int)r.Y;
            if (p.X >= bx && p.X < bx + r.Width && p.Y >= by && p.Y < by + r.Height)
                _widget.ActivateButton();
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
        int tint = (int)Lerp(TintCollapsed, TintExpanded, t);
        float fade = Math.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
        _notch.Render(w, h, r, tint, fade, _widget.DrawContent);
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
