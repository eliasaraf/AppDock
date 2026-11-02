using AppDock.Core.Geometry;

namespace AppDock.Core.Tests;

public class DockGeometryTests
{
    private static readonly Rect Monitor = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(DockEdge.Left, 0, 0, 400, 1080)]
    [InlineData(DockEdge.Right, 1520, 0, 1920, 1080)]
    [InlineData(DockEdge.Top, 0, 0, 1920, 400)]
    [InlineData(DockEdge.Bottom, 0, 680, 1920, 1080)]
    public void BarRect_spans_the_edge(DockEdge edge, int l, int t, int r, int b) =>
        Assert.Equal(new Rect(l, t, r, b), DockGeometry.BarRect(Monitor, edge, 400));

    [Fact]
    public void AdjustAfterQuery_keeps_thickness_when_shell_moves_outer_side()
    {
        // Taskbar on the right: shell moved the bar's right side to 1872.
        var queried = new Rect(1520, 0, 1872, 1080);
        Assert.Equal(new Rect(1472, 0, 1872, 1080), DockGeometry.AdjustAfterQuery(queried, DockEdge.Right, 400));
    }

    [Theory]
    [InlineData(5, 500, DockEdge.Left)]
    [InlineData(1915, 500, DockEdge.Right)]
    [InlineData(900, 3, DockEdge.Top)]
    [InlineData(900, 1078, DockEdge.Bottom)]
    [InlineData(-10, 500, DockEdge.Left)]
    public void HitTestEdge_finds_nearby_edge(int x, int y, DockEdge expected) =>
        Assert.Equal(expected, DockGeometry.HitTestEdge(new PointI(x, y), Monitor, 40));

    [Fact]
    public void HitTestEdge_is_null_in_the_middle() =>
        Assert.Null(DockGeometry.HitTestEdge(new PointI(960, 540), Monitor, 40));

    [Fact]
    public void HitTestEdge_prefers_vertical_edges_at_corners() =>
        Assert.Equal(DockEdge.Left, DockGeometry.HitTestEdge(new PointI(10, 10), Monitor, 40));

    [Fact]
    public void HitTestEdge_ignores_points_far_outside_the_monitor() =>
        Assert.Null(DockGeometry.HitTestEdge(new PointI(-500, 500), Monitor, 40));

    [Fact]
    public void HitTestEdge_uses_monitor_coordinates_on_secondary_screens()
    {
        var second = new Rect(1920, 0, 4480, 1440);
        Assert.Equal(DockEdge.Left, DockGeometry.HitTestEdge(new PointI(1925, 700), second, 40));
        Assert.Equal(DockEdge.Right, DockGeometry.HitTestEdge(new PointI(4470, 700), second, 40));
    }

    [Theory]
    [InlineData(DockEdge.Left, -400, 0, 0, 1080)]
    [InlineData(DockEdge.Right, 1920, 0, 2320, 1080)]
    [InlineData(DockEdge.Top, 0, -400, 1920, 0)]
    [InlineData(DockEdge.Bottom, 0, 1080, 1920, 1480)]
    public void Offscreen_moves_past_the_edge(DockEdge edge, int l, int t, int r, int b)
    {
        var docked = DockGeometry.BarRect(Monitor, edge, 400);
        Assert.Equal(new Rect(l, t, r, b), DockGeometry.Offscreen(docked, edge, Monitor));
    }

    [Fact]
    public void CanSlide_is_false_when_a_neighbour_monitor_is_in_the_way()
    {
        var second = new Rect(1920, 0, 3840, 1080);
        var offscreen = DockGeometry.Offscreen(DockGeometry.BarRect(Monitor, DockEdge.Right, 400), DockEdge.Right, Monitor);
        Assert.False(DockGeometry.CanSlide(offscreen, [Monitor, second]));
        Assert.True(DockGeometry.CanSlide(offscreen, [Monitor]));
    }

    [Fact]
    public void ClampThickness_respects_min_and_max()
    {
        Assert.Equal(160, DockGeometry.ClampThickness(50, Monitor, DockEdge.Left, 1.0));
        Assert.Equal(320, DockGeometry.ClampThickness(50, Monitor, DockEdge.Left, 2.0));
        Assert.Equal(1344, DockGeometry.ClampThickness(5000, Monitor, DockEdge.Left, 1.0));
        Assert.Equal(756, DockGeometry.ClampThickness(5000, Monitor, DockEdge.Top, 1.0));
    }

    [Fact]
    public void UndockRect_keeps_cursor_relative_position_and_stays_in_work_area()
    {
        var current = new Rect(1520, 0, 1920, 1040);
        var cursor = new PointI(1720, 20); // middle of the docked window's title bar
        var r = DockGeometry.UndockRect(current, cursor, new SizeI(1000, 700), new Rect(0, 0, 1920, 1040));
        Assert.Equal(new SizeI(1000, 700), r.Size);
        Assert.True(r.Right <= 1920 && r.Left >= 0 && r.Bottom <= 1040);
        Assert.Equal(20, cursor.Y - r.Top);
    }

    [Fact]
    public void UndockRect_keeps_cursor_at_same_relative_x()
    {
        var current = new Rect(500, 0, 900, 1040);
        var r = DockGeometry.UndockRect(current, new PointI(700, 30), new SizeI(1000, 700), new Rect(0, 0, 1920, 1040));
        Assert.Equal(new Rect(200, 0, 1200, 700), r);
    }

    [Fact]
    public void ClampInto_moves_and_shrinks()
    {
        var area = new Rect(0, 0, 1000, 800);
        Assert.Equal(new Rect(500, 300, 1000, 800), DockGeometry.ClampInto(new Rect(700, 500, 1200, 1000), area));
        Assert.Equal(area, DockGeometry.ClampInto(new Rect(-50, -50, 2000, 2000), area));
    }

    [Fact]
    public void Lerp_and_easing_hit_endpoints()
    {
        var a = new Rect(0, 0, 100, 100);
        var b = new Rect(100, 50, 400, 300);
        Assert.Equal(a, DockGeometry.Lerp(a, b, Easing.OutCubic(0)));
        Assert.Equal(b, DockGeometry.Lerp(a, b, Easing.OutCubic(1)));
        Assert.True(Easing.OutCubic(0.5) > 0.5, "ease-out should be past halfway at t=0.5");
    }

    [Fact]
    public void Rect_expand_and_shrink_round_trip()
    {
        var r = new Rect(10, 10, 110, 210);
        var m = new Margins(7, 0, 7, 7);
        Assert.Equal(r, r.Expand(m).Shrink(m));
        Assert.True(r.NearlyEquals(r.Offset(1, -1)));
        Assert.False(r.NearlyEquals(r.Offset(3, 0)));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, MoveSizeKind.Unknown)] // first pixel of a left-edge resize looks like a move
    [InlineData(-2, 0, 0, 0, MoveSizeKind.Unknown)]
    [InlineData(-3, 0, 0, 0, MoveSizeKind.Resize)]
    [InlineData(0, 0, 0, 40, MoveSizeKind.Resize)]
    [InlineData(-1, 0, -1, 0, MoveSizeKind.Move)] // pure shift, however small
    [InlineData(30, 10, 30, 10, MoveSizeKind.Move)]
    [InlineData(30, 0, 29, 0, MoveSizeKind.Move)] // app adjusts its size by a pixel while moving
    [InlineData(0, 0, 0, 0, MoveSizeKind.Unknown)]
    public void ClassifyMoveSize_waits_until_unambiguous(int dl, int dt, int dr, int db, MoveSizeKind expected)
    {
        var start = new Rect(1480, 0, 1920, 1032);
        var now = new Rect(start.Left + dl, start.Top + dt, start.Right + dr, start.Bottom + db);
        Assert.Equal(expected, DockGeometry.ClassifyMoveSize(start, now));
    }
}
