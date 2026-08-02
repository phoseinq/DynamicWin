using System;

namespace Halo.Widgets;

// What a bar SHOWS, as opposed to what it is. The sources all arrive in jumps - downloaded bytes land per
// network read, an agent's context lands per turn - so a fill drawn straight from its source teleports.
//
// Two rules this must never break, and they are the reason this is a type rather than a lerp written at
// each call site. The shown value converges on the real one, and it never leads it: a bar that runs ahead
// of the truth is an invented number, and inventing numbers has been rejected twice in this project. So
// the step is clamped to the remaining distance rather than eased with an overshooting curve - EaseOutBack
// is right for the pill's geometry and wrong for anything that claims to be a measurement.
//
// Rate per second, not per frame. AdaptFrameRate runs the pill anywhere from 30 to 120fps and the user can
// now cap it, so a per-frame step would make the same download crawl on a machine that is merely busy.
internal struct EasedBar
{
    // Fraction units per second: a full sweep takes ~0.8s, which is long enough to read as movement and
    // short enough that a bar which really did jump is caught up before the eye asks why it is behind.
    private const float PerSecond = 1.25f;

    private float _shown;
    private bool _seeded;
    private long _at;

    internal readonly float Shown => _shown;

    // The Draw* methods are handed a fade, not a delta, and threading one through IWidget for this would
    // change the contract every widget implements for the sake of one field. So the bar times itself. The
    // dt overload below is the real logic and is what the tests drive.
    internal float Step(float target)
    {
        long now = Environment.TickCount64;
        float dt = _at == 0 ? 0.008f : (now - _at) / 1000f;
        _at = now;
        return Step(target, dt);
    }

    // Seeded rather than swept on the first value: a download already at 90% when the pill first draws it
    // did not travel there while anyone was watching, and animating it would be showing something that
    // never happened.
    internal float Step(float target, float dt)
    {
        target = Math.Clamp(target, 0f, 1f);
        if (!_seeded) { _seeded = true; return _shown = target; }

        float step = PerSecond * Math.Clamp(dt, 0f, 0.05f);
        float gap = target - _shown;
        // clamped to what is left, so the shown value lands ON the target and never past it - this is what
        // makes 100% mean 100% and not 100.4% for a frame
        if (Math.Abs(gap) <= step) _shown = target;
        else _shown += Math.Sign(gap) * step;
        return _shown;
    }

    // A slot that has been handed a different item entirely: there is no distance to travel because the
    // previous value was about something else. Sweeping between two unrelated downloads would draw a
    // measurement of nothing.
    internal void Reset() => _seeded = false;
}
