// NotchGeometry.cs — Pure geometry and visibility math: fullscreen detection and the notch show / hide / render decisions, kept free of drawing so it can be unit-tested.

namespace Halo.Shell;

public static class NotchGeometry
{
    public static (int x, int y, int w, int h) CollapsedRect(int workLeft, int workTop, int workWidth, int collapsedWidth, int collapsedHeight)
        => (workLeft + (workWidth - collapsedWidth) / 2, workTop, collapsedWidth, collapsedHeight);

    public static (int x, int y, int w, int h) ExpandedRect(int workLeft, int workTop, int workWidth, int expandedWidth, int expandedHeight)
        => (workLeft + (workWidth - expandedWidth) / 2, workTop, expandedWidth, expandedHeight);
}
