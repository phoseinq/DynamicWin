using System;
using System.Collections.Generic;
using System.Drawing;

namespace Halo.Widgets;

internal readonly record struct AgentNotice(string? State, DateTimeOffset? CompactedAt, string? Message)
{
    internal static AgentNotice None => new(null, null, null);
}

// How hard an agent session is working right now, as one comparable number - the strip sorts a group's
// members by it and the controller uses it to pick WHICH working session takes the pill. Pure so the
// ordering is a test rather than an eyeball. State outranks everything; inside a state, the turn that
// has been running longer wins - between two busy sessions the one deeper into its task is the one the
// user asked to see first. The number is never displayed, only compared, so the elapsed-seconds
// tie-break does not collide with the no-invented-numbers rule.
internal static class AgentActivity
{
    internal static long Rank(string? state, DateTimeOffset? startedAt, DateTimeOffset now)
    {
        int bucket = state switch
        {
            "working" => 5,
            "compacting" => 4,
            "waiting_input" => 3,   // asking beats merely waiting: it needs the user
            "waiting" => 2,
            null or "" or "idle" => 0,
            _ => 1,                 // unknown state still beats a session doing nothing
        };
        if (bucket == 0) return 0;
        long secs = startedAt is { } t ? (long)Math.Clamp((now - t).TotalSeconds, 0, 999_999) : 0;
        return bucket * 1_000_000L + secs;
    }
}

internal interface IWidget
{
    string Icon { get; }                     // glyph fallback for the circle / dropdown

    // Real image for the circle/dropdown (album art, app icon). Overrides the glyph when non-null.
    Bitmap? IconImage => null;

    // optical correction for asymmetric marks in the small app-strip circle; logical pixels
    float IconOffsetX => 0f;

    // Only active widgets show in the pill / swap list. Bumps of Version force a re-render.
    bool IsActive { get; }
    int Version { get; }

    // True when the widget wants continuous frames (e.g. an animated preview). Drives re-render.
    bool Animating => false;

    // True while the widget is playing a short one-shot animation the eye actually tracks (the cover
    // flip). The controller treats it like a morph: full-rate cadence and no collapsed frame-skipping,
    // for the half second it lasts. Keep it rare and brief - this is the expensive kind of frame.
    bool Sprinting => false;

    // Status ring colour for this widget's circle in the strip (null = no ring). Mirrors the
    // collapsed pill's ring (green working, red failed, white idle...).
    Color? Ring => null;

    // If >= 0, the circle's ring is drawn as a progress ARC (0..1) from the top instead of a full ring —
    // used by the download widget so the closed circle fills as the download progresses. < 0 = full ring.
    float RingProgress => -1f;

    // Agent lifecycle events can temporarily expand the pill without coupling the controller to an agent type.
    AgentNotice AgentNotice => AgentNotice.None;

    // AgentActivity.Rank for this widget's session; 0 for everything that is not an agent. Orders a
    // group's members in the strip (busiest first) and breaks the tie when two sessions are working.
    long ActivityRank => 0;

    // pids that mean "the user is inside this session" (agent process + hosting console) —
    // focusing a window with one of these makes the widget primary. Empty = never followed.
    IEnumerable<int> OwnerPids => Array.Empty<int>();

    void DrawContent(Graphics g, int w, int h, float expandFade);       // expanded pill

    // Compact preview drawn in the collapsed pill (w~220, h~40). fade 1=fully collapsed.
    void DrawCollapsed(Graphics g, int w, int h, float fade) { }

    // Clickable regions in the expanded pill (rect in pill-local coords). onClick gets the pill-local
    // click point (for sliders like seek/volume). Empty = none.
    IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h);

    // Same, but for the COLLAPSED pill. It stays empty for almost every widget: the collapsed pill is a
    // glance surface and a stray control there would fire while the user is only reaching for the pill.
    // Put something here only when acting without opening the panel first is the point — stopping a
    // download is the case that earned it.
    IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> CollapsedButtons(int w, int h)
        => Array.Empty<(RectangleF, Action<PointF>)>();
}
