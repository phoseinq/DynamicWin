using System;
using Halo.Interop;
using Windows.System;

namespace Halo.Shell;

internal sealed class NotchController
{
    private const int CollapsedW = 220, CollapsedH = 40, CollapsedR = 20;
    private const int ExpandedW = 560, ExpandedH = 220, ExpandedR = 30;
    private const int TintCollapsed = 235, TintExpanded = 60;
    private const float DurationSeconds = 0.28f;

    private readonly LayeredNotch _notch;
    private readonly DispatcherQueueTimer _timer;
    private readonly int _cl, _ct, _el, _et;

    private float _progress;

    public NotchController(LayeredNotch notch)
    {
        _notch = notch;
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
        int tint = (int)Lerp(TintCollapsed, TintExpanded, t);
        float contentFade = Math.Clamp((t - 0.45f) / 0.55f, 0f, 1f);
        _notch.Render(w, h, r, tint, contentFade);
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
