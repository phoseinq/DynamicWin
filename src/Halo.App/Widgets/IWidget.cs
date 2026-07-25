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
    string Icon { get; }

    Bitmap? IconImage => null;

    bool IsActive { get; }

    /// <summary>
    /// Whether being active also means the pill has something worth showing. Widgets that
    /// reflect a long-lived ambient state rather than an event — a device that stays
    /// connected for hours — return false: they remain reachable in the swap strip and on
    /// hover, but they do not keep the pill open on an otherwise idle desktop.
    /// </summary>
    bool CountsAsContent => true;

    int Version { get; }

    bool Animating => false;

    Color? Ring => null;

    float RingProgress => -1f;

    AgentNotice AgentNotice => AgentNotice.None;

    IEnumerable<int> OwnerPids => Array.Empty<int>();

    void DrawContent(Graphics g, int w, int h, float expandFade);

    void DrawCollapsed(Graphics g, int w, int h, float fade) { }

    IReadOnlyList<(RectangleF rect, Action<PointF> onClick)> Buttons(int w, int h);
}
