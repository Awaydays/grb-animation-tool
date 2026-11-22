namespace GrbAnimTool.Core.Library;

/// <summary>A rectangle of a picture, in pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The part of this rectangle inside a picture of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public PixelRect Within(int width, int height)
    {
        int x0 = Math.Clamp(X, 0, width), y0 = Math.Clamp(Y, 0, height);
        int x1 = Math.Clamp(X + Width, 0, width), y1 = Math.Clamp(Y + Height, 0, height);
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>Checks on a picture of the window before it is saved, so a picture whose 3D view didn't render fails its run instead of
/// passing as a picture of the clip. Pixels are 32-bit values, row by row, <c>width</c> to a row.</summary>
public static class PictureCheck
{
    /// <summary>Why the 3D view in <paramref name="shot"/> can't be what it should show, or null when it looks drawn.
    /// <list type="bullet">
    /// <item>The view's lower quarter is always floor (the camera looks down at the character from above the floor, the floor's grid lines
    /// run through it, and no text lies there): one colour over all of it means the 3D scene wasn't drawn at all.</item>
    /// <item><paramref name="withoutSkeleton"/>, when a skeleton is shown, is the same picture rendered with the skeleton taken out: no pixel
    /// of the view differing from it means the skeleton isn't in the picture.</item>
    /// </list></summary>
    public static string? ViewProblem(ReadOnlySpan<int> shot, ReadOnlySpan<int> withoutSkeleton, int width, int height, PixelRect view)
    {
        view = view.Within(width, height);
        if (view.Width < 4 || view.Height < 8) return $"the 3D view has no size in the picture ({view.Width} x {view.Height} pixels)";
        var floor = new PixelRect(view.X, view.Y + view.Height * 3 / 4, view.Width, view.Height - view.Height * 3 / 4);
        if (OneColour(shot, width, floor)) return "the 3D view is empty: no floor is drawn (its lower part is one colour)";
        if (!withoutSkeleton.IsEmpty && Differences(shot, withoutSkeleton, width, view) == 0) return "the 3D view shows no skeleton";
        return null;
    }

    /// <summary>Whether every pixel of <paramref name="r"/> is the same.</summary>
    public static bool OneColour(ReadOnlySpan<int> px, int width, PixelRect r)
    {
        if (r.IsEmpty) return true;
        int first = px[r.Y * width + r.X];
        for (int y = r.Y; y < r.Y + r.Height; y++)
            if (px.Slice(y * width + r.X, r.Width).ContainsAnyExcept(first)) return false;
        return true;
    }

    /// <summary>How many pixels of <paramref name="r"/> differ between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static int Differences(ReadOnlySpan<int> a, ReadOnlySpan<int> b, int width, PixelRect r)
    {
        int n = 0;
        for (int y = r.Y; y < r.Y + r.Height; y++)
            for (int x = r.X; x < r.X + r.Width; x++)
                if (a[y * width + x] != b[y * width + x]) n++;
        return n;
    }
}
