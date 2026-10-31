using AppDock.Core.Docking;
using AppDock.Core.Geometry;
using static AppDock.Core.Docking.DockEffect;
using static AppDock.Core.Docking.DockEvent;

namespace AppDock.Core.Tests;

public class DockReducerTests
{
    private static DockState DockedVisible(DockEdge edge = DockEdge.Right) =>
        DockState.Initial(edge, null) with { TargetPresent = true };

    private static DockState FloatingVisible() => DockState.Initial(null, null) with { TargetPresent = true };

    private static (DockState State, IReadOnlyList<DockEffect> Effects) Run(DockState s, params DockEvent[] events)
    {
        IReadOnlyList<DockEffect> last = [];
        foreach (var e in events)
        {
            (s, last) = DockReducer.Reduce(s, e);
        }

        return (s, last);
    }

    [Fact]
    public void Initial_state_reserves_nothing_until_target_found()
    {
        var s = DockState.Initial(DockEdge.Left, null);
        Assert.False(s.ReservesSpace);
        var (next, fx) = DockReducer.Reduce(s, new TargetFound(IsVisible: true, IsMinimized: false));
        Assert.True(next.ReservesSpace);
        Assert.Equal<DockEffect>([new MoveToDock(false), new Reserve()], fx);
    }

    [Fact]
    public void Found_hidden_window_is_shown_again()
    {
        var s = DockState.Initial(DockEdge.Left, null) with { Visibility = WindowVisibility.Hidden };
        var (next, fx) = DockReducer.Reduce(s, new TargetFound(IsVisible: false, IsMinimized: false));
        Assert.Equal(WindowVisibility.Visible, next.Visibility);
        Assert.Equal(new DockEffect.Show(), fx[0]);
        Assert.Contains(new Reserve(), fx);
    }

    [Fact]
    public void Docking_moves_the_window_before_reserving()
    {
        var (s, fx) = DockReducer.Reduce(FloatingVisible(), new DockRequested(DockEdge.Left, "M1"));
        Assert.Equal(DockMode.Docked, s.Mode);
        Assert.Equal(DockEdge.Left, s.Edge);
        Assert.Equal("M1", s.Monitor);
        Assert.Equal<DockEffect>([new MoveToDock(true), new Reserve()], fx);
    }

    [Fact]
    public void Undocking_moves_the_window_out_before_releasing()
    {
        var (s, fx) = DockReducer.Reduce(DockedVisible(), new UndockRequested());
        Assert.Equal(DockMode.Floating, s.Mode);
        Assert.False(s.ReservesSpace);
        Assert.Equal<DockEffect>([new MoveToFloat(null, true), new Release()], fx);
    }

    [Fact]
    public void Dragging_a_docked_window_away_releases_then_floats_at_the_drop_point()
    {
        var cursor = new PointI(900, 400);
        var (s, fx) = DockReducer.Reduce(DockedVisible(), new DragEnded(null, null, cursor));
        Assert.Equal(DockMode.Floating, s.Mode);
        Assert.Equal<DockEffect>([new Release(), new MoveToFloat(cursor, true), new SaveFloatingBounds()], fx);
    }

    [Fact]
    public void Dragging_to_another_edge_redocks_there()
    {
        var (s, fx) = DockReducer.Reduce(DockedVisible(DockEdge.Right), new DragEnded(DockEdge.Top, "M2", default));
        Assert.Equal(DockEdge.Top, s.Edge);
        Assert.Equal("M2", s.Monitor);
        Assert.True(s.ReservesSpace);
        Assert.Equal<DockEffect>([new MoveToDock(true), new Reserve()], fx);
    }

    [Fact]
    public void Moving_a_floating_window_only_saves_its_bounds()
    {
        var (s, fx) = DockReducer.Reduce(FloatingVisible(), new DragEnded(null, null, default));
        Assert.Equal(DockMode.Floating, s.Mode);
        Assert.Equal<DockEffect>([new SaveFloatingBounds(), new Release()], fx);
    }

    [Fact]
    public void Hide_slides_out_then_releases_and_show_slides_in_then_reserves()
    {
        var (hidden, hideFx) = DockReducer.Reduce(DockedVisible(), new DockEvent.ToggleVisibility());
        Assert.Equal(WindowVisibility.Hidden, hidden.Visibility);
        Assert.False(hidden.ReservesSpace);
        Assert.Equal<DockEffect>([new SlideOut(), new Release()], hideFx);

        var (shown, showFx) = DockReducer.Reduce(hidden, new DockEvent.ToggleVisibility());
        Assert.True(shown.ReservesSpace);
        Assert.Equal<DockEffect>([new SlideIn(), new Reserve(), new Focus()], showFx);
    }

    [Fact]
    public void Hide_and_show_of_a_floating_window_reserve_nothing()
    {
        var (hidden, hideFx) = DockReducer.Reduce(FloatingVisible(), new DockEvent.ToggleVisibility());
        Assert.Equal<DockEffect>([new DockEffect.Hide()], hideFx);
        var (_, showFx) = DockReducer.Reduce(hidden, new DockEvent.ToggleVisibility());
        Assert.Equal<DockEffect>([new DockEffect.Show(), new Focus()], showFx);
    }

    [Fact]
    public void Minimize_frees_space_and_restore_redocks()
    {
        var (min, minFx) = DockReducer.Reduce(DockedVisible(), new Minimized());
        Assert.Equal(WindowVisibility.Minimized, min.Visibility);
        Assert.False(min.ReservesSpace);
        Assert.Equal<DockEffect>([new Release()], minFx);

        var (restored, restoreFx) = DockReducer.Reduce(min, new Restored());
        Assert.True(restored.ReservesSpace);
        Assert.Equal<DockEffect>([new MoveToDock(false), new Reserve()], restoreFx);
    }

    [Fact]
    public void Toggle_while_minimized_restores_and_focuses()
    {
        var (s, fx) = Run(DockedVisible(), new Minimized(), new DockEvent.ToggleVisibility());
        Assert.Equal(WindowVisibility.Minimized, s.Visibility);
        Assert.Equal<DockEffect>([new DockEffect.Restore(), new Focus()], fx);
    }

    [Fact]
    public void Target_closing_releases_space_and_reopening_redocks()
    {
        var (lost, lostFx) = DockReducer.Reduce(DockedVisible(DockEdge.Left), new TargetLost());
        Assert.False(lost.TargetPresent);
        Assert.Equal(DockMode.Docked, lost.Mode); // layout is remembered
        Assert.Equal<DockEffect>([new Release()], lostFx);

        var (found, foundFx) = DockReducer.Reduce(lost, new TargetFound(true, false));
        Assert.True(found.ReservesSpace);
        Assert.Equal(DockEdge.Left, found.Edge);
        Assert.Equal<DockEffect>([new MoveToDock(false), new Reserve()], foundFx);
    }

    [Fact]
    public void Target_closing_while_hidden_comes_back_visible()
    {
        var (s, _) = Run(DockedVisible(), new DockEvent.ToggleVisibility(), new TargetLost());
        Assert.Equal(WindowVisibility.Visible, s.Visibility);
    }

    [Fact]
    public void App_showing_itself_while_hidden_redocks()
    {
        var (s, fx) = Run(DockedVisible(), new DockEvent.ToggleVisibility(), new ExternallyShown());
        Assert.True(s.ReservesSpace);
        Assert.Equal<DockEffect>([new MoveToDock(false), new Reserve()], fx);
    }

    [Fact]
    public void App_hiding_itself_releases_space()
    {
        var (s, fx) = DockReducer.Reduce(DockedVisible(), new ExternallyHidden());
        Assert.Equal(WindowVisibility.Hidden, s.Visibility);
        Assert.Equal<DockEffect>([new Release()], fx);
    }

    [Fact]
    public void Docking_from_tray_while_hidden_brings_it_back()
    {
        var (s, fx) = Run(FloatingVisible(), new DockEvent.ToggleVisibility(), new DockRequested(DockEdge.Left, null));
        Assert.True(s.ReservesSpace);
        Assert.Equal<DockEffect>([new SlideIn(), new Reserve(), new Focus()], fx);
    }

    [Fact]
    public void Commands_without_a_target_only_change_state()
    {
        var absent = DockState.Initial(null, null);
        var (s, fx) = DockReducer.Reduce(absent, new DockRequested(DockEdge.Bottom, null));
        Assert.Equal(DockEdge.Bottom, s.Edge);
        Assert.Empty(fx);
        Assert.Empty(DockReducer.Reduce(s, new DockEvent.ToggleVisibility()).Effects);
        Assert.Empty(DockReducer.Reduce(s, new Reapply()).Effects);
    }

    [Fact]
    public void Resize_while_docked_reapplies_and_while_floating_saves()
    {
        Assert.Equal<DockEffect>([new MoveToDock(false), new Reserve()], DockReducer.Reduce(DockedVisible(), new ResizeEnded()).Effects);
        Assert.Equal<DockEffect>([new SaveFloatingBounds()], DockReducer.Reduce(FloatingVisible(), new ResizeEnded()).Effects);
    }

    [Fact]
    public void Space_is_reserved_only_when_docked_visible_and_present()
    {
        foreach (var mode in Enum.GetValues<DockMode>())
        {
            foreach (var vis in Enum.GetValues<WindowVisibility>())
            {
                foreach (var present in new[] { true, false })
                {
                    var s = new DockState(mode, DockEdge.Left, null, vis, present);
                    Assert.Equal(mode == DockMode.Docked && vis == WindowVisibility.Visible && present, s.ReservesSpace);
                }
            }
        }
    }

    [Fact]
    public void Random_event_sequences_never_leave_space_reserved_without_a_docked_visible_window()
    {
        // Model check: track "reserved" purely from the emitted effects and compare with the state.
        var events = new DockEvent[]
        {
            new DockRequested(DockEdge.Left, null), new DockRequested(DockEdge.Top, null), new UndockRequested(),
            new DragEnded(null, null, default), new DragEnded(DockEdge.Right, null, default), new ResizeEnded(),
            new DockEvent.ToggleVisibility(), new ShowRequested(), new HideRequested(), new Minimized(), new Restored(),
            new ExternallyShown(), new ExternallyHidden(), new TargetFound(true, false), new TargetLost(), new Reapply(),
        };
        var rng = new Random(1234);
        for (var run = 0; run < 500; run++)
        {
            var s = DockState.Initial(null, null);
            var reserved = false;
            for (var step = 0; step < 40; step++)
            {
                var e = events[rng.Next(events.Length)];
                if (e is TargetFound && s.TargetPresent)
                {
                    continue; // the session never reports Found twice without Lost
                }

                IReadOnlyList<DockEffect> fx;
                (s, fx) = DockReducer.Reduce(s, e);
                foreach (var f in fx)
                {
                    if (f is Reserve)
                    {
                        reserved = true;
                    }
                    else if (f is Release)
                    {
                        reserved = false;
                    }
                }

                if (reserved)
                {
                    Assert.True(s.ReservesSpace, $"run {run} step {step}: reserved after {e.GetType().Name} in {s}");
                }
            }
        }
    }
}
