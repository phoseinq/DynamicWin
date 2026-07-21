using System;
using System.Collections.Generic;
using System.Drawing;

namespace Halo.Widgets;

internal readonly record struct AgentNotice(string? State, DateTimeOffset? CompactedAt, string? Message)
{
    internal static AgentNotice None => new(null, null, null);
}

internal interface IWidget
{
    string Icon { get; }                     // glyph fallback for the circle / dropdown

    // Real image for the circle/dropdown (album art, app icon). Overrides the glyph when non-null.
    Bitmap? IconImage => null;

    // Only active widgets show in the pill / swap list. Bumps of Version force a re-render.
    bool IsActive { get; }
    int Version { get; }

    // True when the widget wants continuous frames (e.g. an animated preview). Drives re-render.
    bool Animating => false;

    // Status ring colour for this widget's circle in the strip (null = no ring). Mirrors the
    // collapsed pill's ring (green working, red failed, white idle...).
    Color? Ring => null;

    // If >= 0, the circle's ring is drawn as a progress ARC (0..1) from the top instead of a full ring —
    // used by the download widget so the closed circle fills as the download progresses. < 0 = full ring.
    float RingProgress => -1f;

    // Agent lifecycle events can temporarily expand the pill without coupling the controller to an agent type.
    AgentNotice AgentNotice => AgentNotice.None;

    // pids that mean "the user is inside this session" (agent process + hosting console) —
    // focusing a window with one of these makes the widget primary. Empty = never followed.
    IEnumerable<int> OwnerPids => Array.Empty<int>();

    void DrawContent(Graphics g, int w, int h, float expandFade);       // expanded pill

    // Compact preview drawn in the collapsed pill (w~220, h~40). fade 1=fully collapsed.
    void DrawCollapsed(Graphics g, int w, int h, float fade) { }

    // Clickable regions in the expanded pill (rect in pill-local coords). onClick gets the pill-local
    // click point (for sliders like seek/volume). Empty = none.
    IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h);
}
