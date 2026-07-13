using System;
using Halo.Interop;
using Halo.Rendering;
using Windows.System;

namespace Halo.Shell;

internal sealed class NotchController
{
    private const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    private const int ExpandedW = 560, ExpandedH = 220, ExpandedR = 30;
    private const float TintCollapsed = 0.9f, TintExpanded = 0.2f;
    private const float DurationSeconds = 0.28f;

    private readonly NotchWindow _window;
    private readonly GlassPill _pill;
    private readonly DispatcherQueueTimer _timer;
    private readonly int _cl, _ct, _el, _et;

    private float _progress;

    public NotchController(NotchWindow window, GlassPill pill)
    {
        _window = window;
        _pill = pill;
        _cl = window.WorkLeft + (window.WorkWidth - CollapsedW) / 2;
        _ct = window.WorkTop;
        _el = window.WorkLeft + (window.WorkWidth - ExpandedW) / 2;
        _et = window.WorkTop;

        Apply(0f);

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
        if (next != _progress)
        {
            _progress = next;
            Apply(_progress);
        }
    }

    private static bool InRect(Win32.POINT p, int left, int top, int w, int h)
        => p.X >= left && p.X < left + w && p.Y >= top && p.Y < top + h;

    private void Apply(float t)
    {
        float e = EaseOutBack(t);
        int w = (int)Lerp(CollapsedW, ExpandedW, e);
        int h = (int)Lerp(CollapsedH, ExpandedH, e);
        int r = (int)Lerp(CollapsedR, ExpandedR, e);
        _window.SetBounds(w, h, r);
        _pill.SetGlass(Lerp(TintCollapsed, TintExpanded, t));
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
