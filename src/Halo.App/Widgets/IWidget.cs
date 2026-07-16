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

    // Agent lifecycle events can temporarily expand the pill without coupling the controller to an agent type.
    AgentNotice AgentNotice => AgentNotice.None;

    void DrawContent(Graphics g, int w, int h, float expandFade);       // expanded pill

    // Compact preview drawn in the collapsed pill (w~220, h~40). fade 1=fully collapsed.
    void DrawCollapsed(Graphics g, int w, int h, float fade) { }

    // Clickable regions in the expanded pill (rect in pill-local coords). onClick gets the pill-local
    // click point (for sliders like seek/volume). Empty = none.
    IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h);
}
