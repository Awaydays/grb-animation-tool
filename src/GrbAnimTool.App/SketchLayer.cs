using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using NVector3 = System.Numerics.Vector3;

namespace GrbAnimTool.App;

/// <summary>A flat drawing over a 3D view, for pictures taken with the window off-screen: a capture there doesn't see the 3D view, so the
/// floor grid and the skeletons are drawn again in 2D through the same camera. <see cref="Paint"/> draws the content.</summary>
sealed class SketchLayer : FrameworkElement
{
    public Action<DrawingContext, Func<NVector3, Point?>>? Paint { get; set; }
    public PerspectiveCamera? Camera { get; set; }
    /// <summary>Where the floor grid's middle stands (whole metres), so the grid stays under a character that travels.</summary>
    public NVector3 FloorCentre { get; set; }
    /// <summary>Whether <see cref="Paint"/> draws: off, the layer shows the floor alone (the picture check's render without the skeletons).</summary>
    public bool PaintContent { get; set; } = true;

    public SketchLayer() => IsHitTestVisible = false;

    /// <summary>A point of the world on this layer by the camera's perspective (its field of view is the horizontal one), or null behind it.</summary>
    Point? Project(NVector3 x, Size size)
    {
        var c = Camera!;
        var look = c.LookDirection; look.Normalize();
        var right = Vector3D.CrossProduct(look, c.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, look);
        var v = new Point3D(x.X, x.Y, x.Z) - c.Position;
        double z = Vector3D.DotProduct(v, look);
        if (z < c.NearPlaneDistance) return null;
        double s = size.Width / 2 / Math.Tan(c.FieldOfView * Math.PI / 360);
        return new Point(size.Width / 2 + Vector3D.DotProduct(v, right) / z * s, size.Height / 2 - Vector3D.DotProduct(v, up) / z * s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Camera is null || Paint is null || RenderSize.Width < 1) return;
        var size = RenderSize;
        dc.PushClip(new RectangleGeometry(new Rect(size)));
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x64, 0x6C)), 1);
        var c = new NVector3(FloorCentre.X, FloorCentre.Y, 0);
        for (int i = -4; i <= 4; i++)
        {
            Line(dc, grid, c + new NVector3(-4, i, 0), c + new NVector3(4, i, 0), size);
            Line(dc, grid, c + new NVector3(i, -4, 0), c + new NVector3(i, 4, 0), size);
        }
        Line(dc, new Pen(Brushes.IndianRed, 2), NVector3.Zero, new NVector3(0.5f, 0, 0), size);
        Line(dc, new Pen(Brushes.MediumSeaGreen, 2), NVector3.Zero, new NVector3(0, 0.5f, 0), size);
        if (PaintContent) Paint(dc, p => Project(p, size));
        dc.Pop();
    }

    /// <summary>Runs <paramref name="render"/> with the content left out (the floor alone), then draws it again.</summary>
    public T WithoutContent<T>(Func<T> render)
    {
        PaintContent = false;
        InvalidateVisual();
        UpdateLayout();
        try { return render(); }
        finally
        {
            PaintContent = true;
            InvalidateVisual();
            UpdateLayout();
        }
    }

    void Line(DrawingContext dc, Pen pen, NVector3 a, NVector3 b, Size size)
    {
        if (Project(a, size) is { } p && Project(b, size) is { } q) dc.DrawLine(pen, p, q);
    }
}
