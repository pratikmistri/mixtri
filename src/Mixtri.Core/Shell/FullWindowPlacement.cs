namespace Mixtri.Core.Shell;

public sealed record FullWindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    public bool IsValid => Width > 0 && Height > 0;

    public FullWindowPlacement FitToWorkArea(int left, int top, int width, int height)
    {
        if (!IsValid || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Window and work-area sizes must be positive.");
        int fittedWidth = Math.Min(Width, width);
        int fittedHeight = Math.Min(Height, height);
        return this with
        {
            X = Math.Clamp(X, left, checked(left + width - fittedWidth)),
            Y = Math.Clamp(Y, top, checked(top + height - fittedHeight)),
            Width = fittedWidth,
            Height = fittedHeight,
        };
    }
}
