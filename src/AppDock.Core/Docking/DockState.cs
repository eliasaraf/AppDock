using AppDock.Core.Geometry;

namespace AppDock.Core.Docking;

public enum DockMode
{
    Floating,
    Docked,
}

public enum WindowVisibility
{
    Visible,
    Hidden,
    Minimized,
}

/// <summary>Complete logical state of one docked target. Immutable; produced by <see cref="DockReducer"/>.</summary>
public sealed record DockState(
    DockMode Mode,
    DockEdge Edge,
    string? Monitor,
    WindowVisibility Visibility,
    bool TargetPresent)
{
    public static DockState Initial(DockEdge? edge, string? monitor) => new(
        edge is null ? DockMode.Floating : DockMode.Docked,
        edge ?? DockEdge.Right,
        monitor,
        WindowVisibility.Visible,
        TargetPresent: false);

    /// <summary>Screen space is reserved only while the docked window is actually on screen.</summary>
    public bool ReservesSpace => TargetPresent && Mode == DockMode.Docked && Visibility == WindowVisibility.Visible;

    public bool IsDocked => Mode == DockMode.Docked;
}

/// <summary>Inputs to the dock state machine.</summary>
public abstract record DockEvent
{
    public sealed record DockRequested(DockEdge Edge, string? Monitor) : DockEvent;

    public sealed record UndockRequested : DockEvent;

    /// <summary>User finished dragging the window. <see cref="Edge"/> is null when dropped away from any edge.</summary>
    public sealed record DragEnded(DockEdge? Edge, string? Monitor, PointI Cursor) : DockEvent;

    public sealed record ResizeEnded : DockEvent;

    public sealed record ToggleVisibility : DockEvent;

    public sealed record ShowRequested : DockEvent;

    public sealed record HideRequested : DockEvent;

    public sealed record Minimized : DockEvent;

    public sealed record Restored : DockEvent;

    /// <summary>The target showed itself (e.g. opened from its own tray icon).</summary>
    public sealed record ExternallyShown : DockEvent;

    /// <summary>The target hid itself (e.g. closed to its tray).</summary>
    public sealed record ExternallyHidden : DockEvent;

    public sealed record TargetFound(bool IsVisible, bool IsMinimized) : DockEvent;

    public sealed record TargetLost : DockEvent;

    /// <summary>Environment changed (display, DPI, Explorer restart, resume): put everything back.</summary>
    public sealed record Reapply : DockEvent;
}

/// <summary>Side effects requested by the state machine, executed in order by <see cref="DockSession"/>.</summary>
public abstract record DockEffect
{
    /// <summary>Move the window into the docked rect (queried from the shell, not yet reserved).</summary>
    public sealed record MoveToDock(bool Animate) : DockEffect;

    /// <summary>Reserve the docked strip (ABM_SETPOS); other windows reflow.</summary>
    public sealed record Reserve : DockEffect;

    /// <summary>Release the strip (ABM_REMOVE).</summary>
    public sealed record Release : DockEffect;

    /// <summary>Move the window to its floating rect; <see cref="DropCursor"/> set when undocked by dragging.</summary>
    public sealed record MoveToFloat(PointI? DropCursor, bool Animate) : DockEffect;

    /// <summary>Slide the docked window out past its edge, then hide it.</summary>
    public sealed record SlideOut : DockEffect;

    /// <summary>Show the docked window and slide it in from its edge.</summary>
    public sealed record SlideIn : DockEffect;

    public sealed record Hide : DockEffect;

    public sealed record Show : DockEffect;

    public sealed record Focus : DockEffect;

    public sealed record Restore : DockEffect;

    public sealed record SaveFloatingBounds : DockEffect;
}
