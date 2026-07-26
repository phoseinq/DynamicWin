using System.Drawing;

namespace Halo.Widgets;

// cursor position in expanded-panel coordinates, set by the controller each tick (for hover effects)
internal static class WidgetInput
{
    public static PointF Mouse;
    public static bool Over; // cursor is inside the fully expanded panel
    // Left button held, so a widget can implement a real press-and-drag. The controller's own click
    // dispatch is edge-triggered — it fires once on press and knows nothing about holding — which is why
    // dragging the seek bar used to be a series of separate jumps instead of a scrub.
    public static bool Down;
}
