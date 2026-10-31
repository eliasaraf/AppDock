namespace AppDock.Core.Geometry;

/// <summary>Pure rectangle math for docking. No Win32 calls, fully unit-testable.</summary>
public static class DockGeometry
{
    public const int MinThicknessDip = 160;

    /// <summary>Largest share of the monitor (along the dock axis) a docked window may take.</summary>
    public const double MaxThicknessFraction = 0.7;

    public static bool IsVertical(DockEdge edge) => edge is DockEdge.Left or DockEdge.Right;

    /// <summary>The full-length strip of <paramref name="thickness"/> pixels along <paramref name="edge"/>.</summary>
    public static Rect BarRect(Rect monitor, DockEdge edge, int thickness) => edge switch
    {
        DockEdge.Left => new Rect(monitor.Left, monitor.Top, monitor.Left + thickness, monitor.Bottom),
        DockEdge.Right => new Rect(monitor.Right - thickness, monitor.Top, monitor.Right, monitor.Bottom),
        DockEdge.Top => new Rect(monitor.Left, monitor.Top, monitor.Right, monitor.Top + thickness),
        DockEdge.Bottom => new Rect(monitor.Left, monitor.Bottom - thickness, monitor.Right, monitor.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>
    /// After ABM_QUERYPOS the shell may move the outer side inward (e.g. another bar on the same edge).
    /// Re-derive the inner side so the bar keeps its thickness.
    /// </summary>
    public static Rect AdjustAfterQuery(Rect queried, DockEdge edge, int thickness) => edge switch
    {
        DockEdge.Left => queried with { Right = queried.Left + thickness },
        DockEdge.Right => queried with { Left = queried.Right - thickness },
        DockEdge.Top => queried with { Bottom = queried.Top + thickness },
        DockEdge.Bottom => queried with { Top = queried.Bottom - thickness },
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>The coordinate of the side touching the screen edge.</summary>
    public static int OuterSide(Rect r, DockEdge edge) => edge switch
    {
        DockEdge.Left => r.Left,
        DockEdge.Right => r.Right,
        DockEdge.Top => r.Top,
        DockEdge.Bottom => r.Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>The coordinate of the side facing the desktop.</summary>
    public static int InnerSide(Rect r, DockEdge edge) => edge switch
    {
        DockEdge.Left => r.Right,
        DockEdge.Right => r.Left,
        DockEdge.Top => r.Bottom,
        DockEdge.Bottom => r.Top,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    public static Rect WithOuterSide(Rect r, DockEdge edge, int value) => edge switch
    {
        DockEdge.Left => r with { Left = value },
        DockEdge.Right => r with { Right = value },
        DockEdge.Top => r with { Top = value },
        DockEdge.Bottom => r with { Bottom = value },
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    public static int ThicknessOf(Rect r, DockEdge edge) => IsVertical(edge) ? r.Width : r.Height;

    /// <summary>Clamp a thickness (pixels) to sensible bounds for the given monitor.</summary>
    public static int ClampThickness(int thickness, Rect monitor, DockEdge edge, double scale)
    {
        var min = (int)Math.Round(MinThicknessDip * scale);
        var axis = IsVertical(edge) ? monitor.Width : monitor.Height;
        var max = Math.Max(min, (int)(axis * MaxThicknessFraction));
        return Math.Clamp(thickness, min, max);
    }

    /// <summary>
    /// Which edge of <paramref name="monitor"/> the cursor is near, if any. Vertical edges win ties at corners,
    /// since side bars are the common case.
    /// </summary>
    public static DockEdge? HitTestEdge(PointI cursor, Rect monitor, int threshold)
    {
        if (!monitor.Expand(new Margins(threshold, threshold, threshold, threshold)).Contains(cursor))
        {
            return null;
        }

        // Ordered so vertical edges win ties (first minimum is kept).
        ReadOnlySpan<(DockEdge Edge, int Distance)> candidates =
        [
            (DockEdge.Left, cursor.X - monitor.Left),
            (DockEdge.Right, monitor.Right - 1 - cursor.X),
            (DockEdge.Top, cursor.Y - monitor.Top),
            (DockEdge.Bottom, monitor.Bottom - 1 - cursor.Y),
        ];

        DockEdge? best = null;
        var bestDistance = int.MaxValue;
        foreach (var (edge, distance) in candidates)
        {
            if (distance <= threshold && distance < bestDistance)
            {
                best = edge;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The rect just past <paramref name="edge"/>, used to slide a docked window out of view.</summary>
    public static Rect Offscreen(Rect docked, DockEdge edge, Rect monitor) => edge switch
    {
        DockEdge.Left => docked.Offset(monitor.Left - docked.Right, 0),
        DockEdge.Right => docked.Offset(monitor.Right - docked.Left, 0),
        DockEdge.Top => docked.Offset(0, monitor.Top - docked.Bottom),
        DockEdge.Bottom => docked.Offset(0, monitor.Bottom - docked.Top),
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    /// <summary>
    /// A slide animation is only clean if the off-screen rect does not land on another monitor
    /// (otherwise the window would visibly pass over the neighbouring screen).
    /// </summary>
    public static bool CanSlide(Rect offscreen, IEnumerable<Rect> monitors) =>
        !monitors.Any(m => m.IntersectsWith(offscreen));

    /// <summary>Move/shrink <paramref name="r"/> so it lies fully inside <paramref name="area"/>.</summary>
    public static Rect ClampInto(Rect r, Rect area)
    {
        var width = Math.Min(r.Width, area.Width);
        var height = Math.Min(r.Height, area.Height);
        var left = Math.Clamp(r.Left, area.Left, area.Right - width);
        var top = Math.Clamp(r.Top, area.Top, area.Bottom - height);
        return Rect.FromXywh(left, top, width, height);
    }

    /// <summary>
    /// Floating rect after the user drags a docked window away: restore the floating size, keep the cursor at the
    /// same relative horizontal position inside the window, and keep the window inside the work area.
    /// </summary>
    public static Rect UndockRect(Rect current, PointI cursor, SizeI floatingSize, Rect workArea)
    {
        var relX = current.Width > 0 ? Math.Clamp((double)(cursor.X - current.Left) / current.Width, 0, 1) : 0.5;
        var grabY = Math.Clamp(cursor.Y - current.Top, 0, Math.Max(0, floatingSize.Height - 1));
        var left = cursor.X - (int)Math.Round(floatingSize.Width * relX);
        var top = cursor.Y - grabY;
        return ClampInto(Rect.FromXywh(left, top, floatingSize.Width, floatingSize.Height), workArea);
    }

    /// <summary>A comfortable default floating rect, centred in the work area.</summary>
    public static Rect DefaultFloatingRect(Rect workArea, double scale)
    {
        var width = Math.Min((int)(1000 * scale), (int)(workArea.Width * 0.8));
        var height = Math.Min((int)(720 * scale), (int)(workArea.Height * 0.8));
        return Rect.FromXywh(
            workArea.Left + ((workArea.Width - width) / 2),
            workArea.Top + ((workArea.Height - height) / 2),
            width,
            height);
    }

    public static Rect Lerp(Rect from, Rect to, double t) => new(
        Lerp(from.Left, to.Left, t),
        Lerp(from.Top, to.Top, t),
        Lerp(from.Right, to.Right, t),
        Lerp(from.Bottom, to.Bottom, t));

    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + ((b - a) * t));
}

/// <summary>Easing curves for window animations.</summary>
public static class Easing
{
    /// <summary>Decelerating curve, similar to Windows' own window transitions.</summary>
    public static double OutCubic(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var inv = 1 - t;
        return 1 - (inv * inv * inv);
    }
}
