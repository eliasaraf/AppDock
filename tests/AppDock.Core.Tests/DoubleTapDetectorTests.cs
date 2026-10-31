using AppDock.Core.Input;

namespace AppDock.Core.Tests;

public class DoubleTapDetectorTests
{
    private readonly DoubleTapDetector _d = new(intervalMs: 300, maxHoldMs: 350);

    private bool Tap(long downAt, long upAt)
    {
        _d.KeyDown(true, downAt);
        return _d.KeyUp(true, upAt);
    }

    [Fact]
    public void Two_quick_taps_trigger()
    {
        Assert.False(Tap(0, 60));
        Assert.True(Tap(200, 260));
    }

    [Fact]
    public void Third_tap_starts_a_new_sequence()
    {
        Assert.False(Tap(0, 60));
        Assert.True(Tap(200, 260));
        Assert.False(Tap(400, 460));
        Assert.True(Tap(600, 660));
    }

    [Fact]
    public void Slow_second_tap_does_not_trigger_but_starts_a_new_sequence()
    {
        Assert.False(Tap(0, 60));
        Assert.False(Tap(500, 560));
        Assert.True(Tap(700, 760));
    }

    [Fact]
    public void Holding_the_key_does_not_trigger()
    {
        Assert.False(Tap(0, 60));
        Assert.False(Tap(200, 900));
    }

    [Fact]
    public void Key_repeat_is_ignored()
    {
        Assert.False(Tap(0, 60));
        _d.KeyDown(true, 200);
        _d.KeyDown(true, 230);
        _d.KeyDown(true, 260);
        Assert.True(_d.KeyUp(true, 280));
    }

    [Fact]
    public void Chord_during_second_tap_cancels()
    {
        Assert.False(Tap(0, 60));
        _d.KeyDown(true, 200);
        _d.KeyDown(false, 220); // Ctrl+C
        _d.KeyUp(false, 240);
        Assert.False(_d.KeyUp(true, 260));
        Assert.False(Tap(300, 340));
    }

    [Fact]
    public void Other_key_between_taps_cancels()
    {
        Assert.False(Tap(0, 60));
        _d.KeyDown(false, 100);
        _d.KeyUp(false, 120);
        Assert.False(Tap(200, 260));
    }

    [Fact]
    public void Mouse_click_cancels()
    {
        Assert.False(Tap(0, 60));
        _d.KeyDown(true, 200);
        _d.MouseButton(); // Ctrl+click
        Assert.False(_d.KeyUp(true, 260));
    }

    [Fact]
    public void Release_without_press_is_ignored() => Assert.False(_d.KeyUp(true, 100));
}
