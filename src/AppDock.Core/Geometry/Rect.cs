namespace AppDock.Core.Geometry;

/// <summary>Screen edge a window can be docked to. Values match the Win32 ABE_* constants.</summary>
public enum DockEdge
{
    Left = 0,
    Top = 1,
    Right = 2,
    Bottom = 3,
}

/// <summary>Integer point in physical screen pixels.</summary>
public readonly record struct PointI(int X, int Y);

/// <summary>Integer size in physical pixels.</summary>
public readonly record struct SizeI(int Width, int Height);

/// <summary>Per-side distances, e.g. the invisible DWM resize borders around a window.</summary>
public readonly record struct Margins(int Left, int Top, int Right, int Bottom)
{
    public static Margins Zero => default;
}

/// <summary>Immutable rectangle in physical screen pixels (right/bottom exclusive).</summary>
public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public SizeI Size => new(Width, Height);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PointI Center => new(Left + (Width / 2), Top + (Height / 2));

    public static Rect FromXywh(int x, int y, int width, int height) => new(x, y, x + width, y + height);

    public bool Contains(PointI p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public bool IntersectsWith(Rect other) =>
        other.Left < Right && Left < other.Right && other.Top < Bottom && Top < other.Bottom;

    public Rect Offset(int dx, int dy) => new(Left + dx, Top + dy, Right + dx, Bottom + dy);

    public Rect Expand(Margins m) => new(Left - m.Left, Top - m.Top, Right + m.Right, Bottom + m.Bottom);

    public Rect Shrink(Margins m) => new(Left + m.Left, Top + m.Top, Right - m.Right, Bottom - m.Bottom);

    /// <summary>True when every side is within <paramref name="tolerance"/> pixels of <paramref name="other"/>.</summary>
    public bool NearlyEquals(Rect other, int tolerance = 1) =>
        Math.Abs(Left - other.Left) <= tolerance && Math.Abs(Top - other.Top) <= tolerance &&
        Math.Abs(Right - other.Right) <= tolerance && Math.Abs(Bottom - other.Bottom) <= tolerance;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}
