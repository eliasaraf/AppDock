using AppDock.Core.Geometry;
using static AppDock.Core.Docking.DockEffect;
using static AppDock.Core.Docking.DockEvent;

namespace AppDock.Core.Docking;

/// <summary>
/// Pure state machine: (state, event) -> (state, effects). Effect order is deliberate: when docking the window moves
/// into place first and space is reserved afterwards; when leaving, the window leaves the strip before it is
/// released. That way other windows reflow exactly once and never flash over a half-moved window.
/// </summary>
public static class DockReducer
{
    private static readonly DockEffect[] None = [];

    public static (DockState State, IReadOnlyList<DockEffect> Effects) Reduce(DockState s, DockEvent e) => e switch
    {
        DockRequested d => Dock(s, d.Edge, d.Monitor, fromUser: true),
        DragEnded { Edge: { } edge } d => Dock(s, edge, d.Monitor, fromUser: false),
        DragEnded d => DragAway(s, d.Cursor),
        UndockRequested => Undock(s),
        ResizeEnded => Resized(s),
        ToggleVisibility => s.Visibility switch
        {
            WindowVisibility.Visible => Hide(s),
            WindowVisibility.Hidden => Show(s),
            _ => Present(s, [new Restore(), new Focus()]),
        },
        ShowRequested => s.Visibility switch
        {
            WindowVisibility.Visible => Present(s, [new Focus()]),
            WindowVisibility.Hidden => Show(s),
            _ => Present(s, [new Restore(), new Focus()]),
        },
        HideRequested => s.Visibility == WindowVisibility.Visible ? Hide(s) : (s, None),
        Minimized => s.Visibility == WindowVisibility.Visible
            ? (s with { Visibility = WindowVisibility.Minimized }, s.ReservesSpace ? [new Release()] : None)
            : (s, None),
        Restored => s.Visibility == WindowVisibility.Minimized ? BackOnScreen(s) : (s, None),
        ExternallyShown => s.Visibility == WindowVisibility.Hidden ? BackOnScreen(s) : (s, None),
        ExternallyHidden => s.Visibility == WindowVisibility.Visible
            ? (s with { Visibility = WindowVisibility.Hidden }, s.ReservesSpace ? [new Release()] : None)
            : (s, None),
        TargetFound f => Found(s, f),
        TargetLost => (s with { TargetPresent = false, Visibility = WindowVisibility.Visible },
            s.ReservesSpace ? [new Release()] : None),
        Reapply => (s, s.ReservesSpace ? [new MoveToDock(Animate: false), new Reserve()] : None),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e, "Unknown dock event."),
    };

    private static (DockState, IReadOnlyList<DockEffect>) Dock(DockState s, DockEdge edge, string? monitor, bool fromUser)
    {
        var next = s with { Mode = DockMode.Docked, Edge = edge, Monitor = monitor ?? s.Monitor };
        if (!s.TargetPresent)
        {
            return (next, None);
        }

        return s.Visibility switch
        {
            WindowVisibility.Visible => (next, [new MoveToDock(Animate: true), new Reserve()]),

            // Docking from the tray while hidden brings the window back, already in its new place.
            WindowVisibility.Hidden when fromUser =>
                (next with { Visibility = WindowVisibility.Visible }, [new SlideIn(), new Reserve(), new Focus()]),

            // Minimized: restoring raises Restored, which docks it.
            WindowVisibility.Minimized when fromUser => (next, [new Restore()]),
            _ => (next, None),
        };
    }

    private static (DockState, IReadOnlyList<DockEffect>) DragAway(DockState s, PointI cursor)
    {
        if (s.IsDocked && s.TargetPresent && s.Visibility == WindowVisibility.Visible)
        {
            // The window already left the strip under the user's hand; free the space first, then settle it.
            return (s with { Mode = DockMode.Floating },
                [new Release(), new MoveToFloat(cursor, Animate: true), new SaveFloatingBounds()]);
        }

        // Plain move of a floating window. Release drops the query-only registration made for the snap preview.
        return (s, [new SaveFloatingBounds(), new Release()]);
    }

    private static (DockState, IReadOnlyList<DockEffect>) Undock(DockState s)
    {
        if (!s.IsDocked)
        {
            return (s, None);
        }

        var next = s with { Mode = DockMode.Floating };
        return s.ReservesSpace
            ? (next, [new MoveToFloat(null, Animate: true), new Release()])
            : (next, None);
    }

    private static (DockState, IReadOnlyList<DockEffect>) Resized(DockState s)
    {
        if (s.ReservesSpace)
        {
            return (s, [new MoveToDock(Animate: false), new Reserve()]);
        }

        return s is { Mode: DockMode.Floating, TargetPresent: true, Visibility: WindowVisibility.Visible }
            ? (s, [new SaveFloatingBounds()])
            : (s, None);
    }

    private static (DockState, IReadOnlyList<DockEffect>) Hide(DockState s)
    {
        if (!s.TargetPresent)
        {
            return (s, None);
        }

        var next = s with { Visibility = WindowVisibility.Hidden };
        return s.IsDocked ? (next, [new SlideOut(), new Release()]) : (next, [new Hide()]);
    }

    private static (DockState, IReadOnlyList<DockEffect>) Show(DockState s)
    {
        if (!s.TargetPresent)
        {
            return (s, None);
        }

        var next = s with { Visibility = WindowVisibility.Visible };
        return s.IsDocked
            ? (next, [new SlideIn(), new Reserve(), new Focus()])
            : (next, [new Show(), new Focus()]);
    }

    private static (DockState, IReadOnlyList<DockEffect>) BackOnScreen(DockState s)
    {
        var next = s with { Visibility = WindowVisibility.Visible };
        return (next, next.ReservesSpace ? [new MoveToDock(Animate: false), new Reserve()] : None);
    }

    private static (DockState, IReadOnlyList<DockEffect>) Found(DockState s, TargetFound f)
    {
        var next = s with
        {
            TargetPresent = true,
            Visibility = f.IsMinimized ? WindowVisibility.Minimized : WindowVisibility.Visible,
        };

        var effects = new List<DockEffect>();
        if (!f.IsVisible)
        {
            // Recovery: the window was left hidden (e.g. AppDock was killed while it was hidden).
            effects.Add(new Show());
        }

        if (next.ReservesSpace)
        {
            effects.Add(new MoveToDock(Animate: false));
            effects.Add(new Reserve());
        }

        return (next, effects);
    }

    private static (DockState, IReadOnlyList<DockEffect>) Present(DockState s, DockEffect[] effects) =>
        s.TargetPresent ? (s, effects) : (s, None);
}
