// WidgetInput.cs — Shared hover and mouse-position state handed to widgets while they draw.

using System.Drawing;

namespace Halo.Widgets;

internal static class WidgetInput
{
    public static PointF Mouse;
    public static bool Over;
}
