using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace GrbAnimTool.App;

/// <summary>A perspective camera that orbits a target point with z up: left drag turns around the target, right or middle drag pans,
/// the wheel zooms. The view is held as yaw (degrees about z, 0 looking from +x), pitch (degrees above the floor) and distance.</summary>
sealed class OrbitCamera
{
    readonly FrameworkElement _surface;
    Point _last;
    MouseButton? _drag;

    public PerspectiveCamera Camera { get; } = new() { FieldOfView = 45, NearPlaneDistance = 0.05, FarPlaneDistance = 500, UpDirection = new Vector3D(0, 0, 1) };
    public Point3D Target { get; private set; }
    public double Yaw { get; private set; } = 60;
    public double Pitch { get; private set; } = 10;
    public double Distance { get; private set; } = 4.2;

    public OrbitCamera(FrameworkElement surface)
    {
        _surface = surface;
        surface.MouseDown += OnDown;
        surface.MouseMove += OnMove;
        surface.MouseUp += OnUp;
        surface.LostMouseCapture += (_, _) => _drag = null;
        surface.MouseWheel += OnWheel;
        Apply();
    }

    /// <summary>Looks at <paramref name="target"/> from the given direction and distance.</summary>
    public void Set(Point3D target, double yaw, double pitch, double distance)
    {
        Target = target;
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -89, 89);
        Distance = Math.Clamp(distance, 0.2, 200);
        Apply();
    }

    /// <summary>Moves the target and the camera together.</summary>
    public void Move(Vector3D by)
    {
        Target += by;
        Apply();
    }

    void Apply()
    {
        double y = Yaw * Math.PI / 180, p = Pitch * Math.PI / 180;
        var offset = new Vector3D(Math.Cos(p) * Math.Cos(y), Math.Cos(p) * Math.Sin(y), Math.Sin(p)) * Distance;
        Camera.Position = Target + offset;
        Camera.LookDirection = -offset;
        Camera.UpDirection = new Vector3D(0, 0, 1);
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not null) return;
        _drag = e.ChangedButton;
        _last = e.GetPosition(_surface);
        _surface.CaptureMouse();
        e.Handled = true;
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != _drag) return;
        _drag = null;
        _surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag is null) return;
        var now = e.GetPosition(_surface);
        double dx = now.X - _last.X, dy = now.Y - _last.Y;
        _last = now;
        if (_drag == MouseButton.Left)
        {
            Yaw -= dx * 0.4;
            Pitch = Math.Clamp(Pitch + dy * 0.3, -89, 89);
        }
        else
        {
            // one pixel moves the target by the size of a pixel at the target's distance
            double s = 2 * Distance * Math.Tan(Camera.FieldOfView * Math.PI / 360) / Math.Max(1, _surface.ActualHeight);
            var look = Camera.LookDirection; look.Normalize();
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1)); right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            Target += -right * dx * s + up * dy * s;
        }
        Apply();
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        Distance = Math.Clamp(Distance * Math.Pow(0.9, e.Delta / 120.0), 0.2, 200);
        Apply();
        e.Handled = true;
    }
}
