using System.Drawing;

namespace Halo.Widgets;

// cursor position in expanded-panel coordinates, set by the controller each tick (for hover effects)
internal static class WidgetInput
{
    public static PointF Mouse;
    public static bool Over; // cursor is inside the fully expanded panel
}
