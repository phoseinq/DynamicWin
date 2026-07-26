using Halo.Shell;

namespace Halo.Tests;

public class NotchGeometryTests
{
    [Fact]
    public void CollapsedRect_is_horizontally_centered_and_top_pinned()
    {
        var r = NotchGeometry.CollapsedRect(workLeft: 0, workTop: 0, workWidth: 1920, collapsedWidth: 220, collapsedHeight: 34);
        Assert.Equal((1920 - 220) / 2, r.x);
        Assert.Equal(0, r.y);
        Assert.Equal(220, r.w);
        Assert.Equal(34, r.h);
    }

    [Fact]
    public void CollapsedRect_respects_nonzero_work_origin()
    {
        var r = NotchGeometry.CollapsedRect(workLeft: -1920, workTop: 0, workWidth: 1920, collapsedWidth: 200, collapsedHeight: 30);
        Assert.Equal(-1920 + (1920 - 200) / 2, r.x);
    }
}
