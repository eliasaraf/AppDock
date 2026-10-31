namespace AppDock.Core.Input;

/// <summary>
/// Recognises a clean double tap of one modifier key: press-release, press-release, with nothing else pressed in
/// between, each tap short, and the second press soon after the first release. Holding the key, key repeat,
/// chords (Ctrl+C), mouse clicks (Ctrl+click) and slow taps never trigger. Pure logic; timestamps in ms.
/// </summary>
public sealed class DoubleTapDetector
{
    private bool _down;
    private long _downAt;
    private bool _tainted;
    private long? _firstTapUpAt;

    public DoubleTapDetector(int intervalMs = 300, int maxHoldMs = 350)
    {
        IntervalMs = intervalMs;
        MaxHoldMs = maxHoldMs;
    }

    /// <summary>Max time between the first release and the second press.</summary>
    public int IntervalMs { get; set; }

    /// <summary>Max time a single tap may be held down.</summary>
    public int MaxHoldMs { get; set; }

    public void KeyDown(bool isTapKey, long now)
    {
        if (!isTapKey)
        {
            Reset();
            _tainted = _down;
            return;
        }

        if (_down)
        {
            return; // auto-repeat while held
        }

        _down = true;
        _downAt = now;
        _tainted = false;
        if (_firstTapUpAt is { } up && now - up > IntervalMs)
        {
            _firstTapUpAt = null;
        }
    }

    /// <summary>Returns true when this release completes a double tap.</summary>
    public bool KeyUp(bool isTapKey, long now)
    {
        if (!isTapKey || !_down)
        {
            return false;
        }

        _down = false;
        if (_tainted || now - _downAt > MaxHoldMs)
        {
            _firstTapUpAt = null;
            return false;
        }

        if (_firstTapUpAt is { } firstUp && _downAt - firstUp <= IntervalMs)
        {
            _firstTapUpAt = null;
            return true;
        }

        _firstTapUpAt = now;
        return false;
    }

    /// <summary>Any mouse button press cancels a tap in progress (e.g. Ctrl+click).</summary>
    public void MouseButton()
    {
        _firstTapUpAt = null;
        if (_down)
        {
            _tainted = true;
        }
    }

    public void Reset()
    {
        _firstTapUpAt = null;
        _tainted = false;
    }
}
