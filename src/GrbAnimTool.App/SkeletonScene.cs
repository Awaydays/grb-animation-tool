using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using GrbAnimTool.Core.Hashing;
using GrbAnimTool.Core.Library;
using GrbAnimTool.Core.Skeletons;
using NVector3 = System.Numerics.Vector3;
using Transform = GrbAnimTool.Core.Skeletons.Transform;

namespace GrbAnimTool.App;

/// <summary>How the Edit and Import screens show two clips: one over the other (the reference as a ghost behind the other), or side by
/// side, apart sideways so they never overlap.</summary>
public enum CompareLayout { Overlay, SideBySide }

/// <summary>The two clips' places for a <see cref="CompareLayout"/>.</summary>
static class Compare
{
    /// <summary>How far each skeleton stands from the middle side by side (metres): the reference at +x, the other at -x, so seen from
    /// the front (the characters face +y) the reference stands on the left.</summary>
    public const double Apart = 0.55;

    /// <summary>The sideways offset of the reference (<paramref name="reference"/>) or the other skeleton in a layout.</summary>
    public static double Offset(CompareLayout layout, bool reference) => layout == CompareLayout.SideBySide ? (reference ? Apart : -Apart) : 0;

    /// <summary>The camera's distance for a layout: farther side by side, to hold both.</summary>
    public static double Distance(CompareLayout layout) => layout == CompareLayout.SideBySide ? 4.6 : 3.8;

    /// <summary>The layout box's items, in <see cref="CompareLayout"/>'s order.</summary>
    public static readonly string[] Names = ["Overlay", "Side by side"];
}

/// <summary>The bare skeleton as 3D models: a thin stick from each bone's parent to it, a small sphere at each joint, the add-on's bones
/// in another colour, and an arrow on the floor for the mover (where the character stands and which way it faces). The meshes are built
/// once; a new pose only changes their transforms. Bones hanging from a root (a frame at the floor, not a body part) get no stick, and
/// neither do add-on bones hanging from a body bone: that link is an attachment offset, not a limb, and some clips park those bones at
/// fixed points in their world, far from the body.</summary>
sealed class SkeletonScene
{
    static readonly MeshGeometry3D UnitStick = Cylinder(8);
    static readonly MeshGeometry3D UnitSphere = Sphere(10, 7);
    static readonly MeshGeometry3D Arrow = ArrowMesh();
    /// <summary>A transform that shrinks a mesh to a point: a bone with no valid place is drawn as nothing.</summary>
    static readonly Matrix3D Nothing = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    readonly Skeleton _skeleton;
    readonly MatrixTransform3D[] _joint;
    readonly double[] _jointRadius;
    readonly MatrixTransform3D?[] _stick;
    readonly double[] _stickRadius;
    readonly MatrixTransform3D _arrow = new();
    readonly NVector3[] _at;
    /// <summary>Each joint's height in the clip's world (the mover applied), whatever the view draws.</summary>
    readonly float[] _worldZ;
    readonly bool[] _finite;
    readonly int _hips;
    // the add-on's bones: each one's joint and stick models, shown per clip
    readonly GeometryModel3D?[] _addOnJoint;
    readonly GeometryModel3D?[] _addOnStick;
    readonly Model3DGroup _addOn = new();

    public ModelVisual3D Visual { get; } = new();

    /// <summary>The hips' place in the last pose drawn (the character's centre, for the camera to follow).</summary>
    public NVector3 Centre { get; private set; } = new(0, 0, 0.95f);

    /// <summary>How many bones the last pose couldn't place (a transform that isn't finite numbers) and so didn't draw.</summary>
    public int NotDrawn { get; private set; }

    /// <summary>The skeleton's models; <paramref name="second"/> draws the body in green instead of blue, for a second skeleton beside the
    /// first (the Import screen's imported clip next to the game's); <paramref name="ghost"/> draws it pale and see-through, for the
    /// original behind an edit's result (the Edit screen).</summary>
    public SkeletonScene(Skeleton skeleton, bool second = false, bool ghost = false)
    {
        _skeleton = skeleton;
        _second = second;
        _ghost = ghost;
        int n = skeleton.Bones.Count;
        _joint = new MatrixTransform3D[n];
        _jointRadius = new double[n];
        _stick = new MatrixTransform3D?[n];
        _stickRadius = new double[n];
        _at = new NVector3[n];
        _worldZ = new float[n];
        _finite = new bool[n];
        _addOnJoint = new GeometryModel3D?[n];
        _addOnStick = new GeometryModel3D?[n];
        _hips = skeleton.IndexOf(Crc32.Of("Hips"));

        Material M(Color c) => ghost ? Ghostly(c) : Shiny(c);
        // a ghost's joints and sticks are thinner than a solid skeleton's: where the two stand in the same place the ghost lies inside the
        // solid one, so the solid one's surface is always nearer the camera and the two never fight over which is drawn in front
        double thick = ghost ? GhostThickness : 1;
        var bodyStick = M(BodyStickColour);
        var addOnStick = M(Color.FromRgb(0xF5, 0xB0, 0x41));
        var bodyJoint = M(BodyJointColour);
        var addOnJoint = M(Color.FromRgb(0xE8, 0x6A, 0x10));
        var rootJoint = M(Color.FromRgb(0x90, 0x90, 0x90));

        var group = new Model3DGroup();
        for (int i = 0; i < n; i++)
        {
            var b = skeleton.Bones[i];
            double len = b.ParentIndex >= 0 ? (b.LocalRest.Position).Length() : 0;
            _jointRadius[i] = thick * (b.IsRoot ? 0.03 : Math.Clamp(0.3 * len, 0.006, 0.022));
            _joint[i] = new MatrixTransform3D();
            var jm = b.IsRoot ? rootJoint : b.FromAddOn ? addOnJoint : bodyJoint;
            var joint = new GeometryModel3D(UnitSphere, jm) { BackMaterial = jm, Transform = _joint[i] };
            if (b.FromAddOn) _addOnJoint[i] = joint; else group.Children.Add(joint);

            if (b.ParentIndex < 0) continue;
            var parent = skeleton.Bones[b.ParentIndex];
            if (parent.IsRoot || (b.FromAddOn && !parent.FromAddOn)) continue;
            _stickRadius[i] = thick * Math.Clamp(0.1 * len, 0.003, 0.011);
            _stick[i] = new MatrixTransform3D();
            var sm = b.FromAddOn ? addOnStick : bodyStick;
            var stick = new GeometryModel3D(UnitStick, sm) { BackMaterial = sm, Transform = _stick[i] };
            if (b.FromAddOn) _addOnStick[i] = stick; else group.Children.Add(stick);
        }
        var am = M(Color.FromRgb(0x3C, 0xC8, 0x6E));
        group.Children.Add(new GeometryModel3D(Arrow, am) { BackMaterial = am, Transform = _arrow });
        _head = skeleton.IndexOf(Crc32.Of("Head"));
        var nm = M(Color.FromRgb(0xE0, 0x50, 0x50));
        group.Children.Add(new GeometryModel3D(UnitStick, nm) { BackMaterial = nm, Transform = _nose });
        group.Children.Add(_addOn);
        // the feet's marks: a ring of colour round each foot joint, shown while the foot is planted or below the floor
        _feet = ClipMarks.FootBones.Select(side => side.Select(n => skeleton.IndexOf(Crc32.Of(n))).ToArray()).ToArray();
        _footMark = new GeometryModel3D[_feet.Sum(f => f.Length)];
        for (int k = 0; k < _footMark.Length; k++)
        {
            _footMark[k] = new GeometryModel3D(UnitSphere, PlantedMaterial) { BackMaterial = PlantedMaterial, Transform = new MatrixTransform3D(Nothing) };
            group.Children.Add(_footMark[k]);
        }
        Visual.Content = group;
    }

    /// <summary>How thick a ghost's joints and sticks are against a solid skeleton's.</summary>
    public const double GhostThickness = 0.7;

    /// <summary>How far below the floor a ghost's mover arrow lies (metres), so a solid skeleton's arrow in the same place is drawn over it.</summary>
    public const double GhostArrowDrop = 0.003;

    /// <summary>Whether this is a ghost: pale, see-through and thinner (<see cref="GhostThickness"/>).</summary>
    public bool IsGhost => _ghost;

    /// <summary>A joint's radius as drawn (for checking the ghost against a solid skeleton).</summary>
    public double JointRadius(int bone) => _jointRadius[bone];

    /// <summary>A stick's radius as drawn, 0 for a bone with no stick.</summary>
    public double StickRadius(int bone) => _stickRadius[bone];

    /// <summary>Every material the skeleton's models are drawn in, front and back.</summary>
    public IEnumerable<Material> Materials()
    {
        static IEnumerable<GeometryModel3D> All(Model3D m) => m switch
        {
            GeometryModel3D g => [g],
            Model3DGroup grp => grp.Children.SelectMany(All),
            _ => [],
        };
        return All((Model3D)Visual.Content).Concat(_addOnJoint.OfType<GeometryModel3D>()).Concat(_addOnStick.OfType<GeometryModel3D>())
            .SelectMany(g => new[] { g.Material, g.BackMaterial }).Where(m => m is not null && m != PlantedMaterial && m != BelowFloorMaterial);
    }

    /// <summary>The mover arrow's height above the floor as drawn.</summary>
    public double ArrowHeight => _arrow.Matrix.OffsetZ;

    Color BodyStickColour => _ghost ? Color.FromRgb(0xC8, 0xC8, 0xC8) : _second ? Color.FromRgb(0xD6, 0xEC, 0xD2) : Color.FromRgb(0xDC, 0xE3, 0xEC);
    Color BodyJointColour => _ghost ? Color.FromRgb(0xA0, 0xA0, 0xA0) : _second ? Color.FromRgb(0x3F, 0xB0, 0x5A) : Color.FromRgb(0x4F, 0x8F, 0xD8);

    static readonly Color PlantedColour = Color.FromRgb(0x30, 0xD0, 0xE0), BelowFloorColour = Color.FromRgb(0xE8, 0x30, 0x30);
    static readonly Material PlantedMaterial = Ghostly(PlantedColour, 0x90), BelowFloorMaterial = Ghostly(BelowFloorColour, 0xC0);

    readonly bool _ghost;
    readonly int[][] _feet;
    readonly GeometryModel3D[] _footMark;
    // per foot joint, in _footMark's order: 0 no mark, 1 planted, 2 below the floor
    readonly int[] _footState = new int[4];

    /// <summary>Marks the feet of the last pose drawn: a joint below the floor (<see cref="ClipMarks.BelowFloor"/>) red, else a planted
    /// foot's joints (by <paramref name="planted"/>, left then right) cyan; with <paramref name="show"/> false, none.</summary>
    public void MarkFeet(bool leftPlanted, bool rightPlanted, bool show = true)
    {
        int k = 0;
        for (int side = 0; side < _feet.Length; side++)
            foreach (int b in _feet[side])
            {
                int state = 0;
                // below the floor by the joint's height in the clip's world (mover applied), as the floor check measures it,
                // whether or not the view applies the mover
                if (show && b >= 0 && _finite[b])
                    state = ClipMarks.BelowFloor(_worldZ[b]) ? 2 : (side == 0 ? leftPlanted : rightPlanted) ? 1 : 0;
                if (k < _footState.Length) _footState[k] = state;
                var m = _footMark[k];
                var mat = state == 2 ? BelowFloorMaterial : PlantedMaterial;
                if (m.Material != mat) { m.Material = mat; m.BackMaterial = mat; }
                double r = 0.04;
                ((MatrixTransform3D)m.Transform).Matrix = state == 0 ? Nothing : new Matrix3D(r, 0, 0, 0, 0, r, 0, 0, 0, 0, r, 0, _at[b].X, _at[b].Y, _at[b].Z, 1);
                k++;
            }
    }

    /// <summary>How many foot joints the last <see cref="MarkFeet"/> drew red (below the floor).</summary>
    public int FeetBelowFloor => _footState.Count(s => s == 2);

    /// <summary>Shows the add-on bones that <paramref name="tracks"/> (a clip's track hashes) move: each bone the clip keys, and the
    /// add-on bones under it. An add-on bone the clip leaves alone, with no keyed add-on bone above it, stays at its rest offset from the
    /// body, which says nothing about the clip, so it is hidden. A stick joins two add-on bones only when both show.</summary>
    public void ShowAddOnFor(IEnumerable<uint> tracks)
    {
        var bones = _skeleton.Bones;
        var keyed = new HashSet<int>();
        foreach (var h in tracks)
            if (h != 0 && _skeleton.IndexOf(h) is int k and >= 0 && bones[k].FromAddOn) keyed.Add(k);
        var shown = new bool[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            if (!bones[i].FromAddOn) continue;
            for (int a = i; a >= 0 && bones[a].FromAddOn; a = bones[a].ParentIndex)
                if (keyed.Contains(a)) { shown[i] = true; break; }
        }
        _addOn.Children.Clear();
        _addOnShown = shown;
        for (int i = 0; i < bones.Count; i++)
        {
            if (!shown[i]) continue;
            _addOn.Children.Add(_addOnJoint[i]!);
            if (_addOnStick[i] is { } st && shown[bones[i].ParentIndex]) _addOn.Children.Add(st);
        }
    }

    bool[] _addOnShown = [];
    readonly bool _second;
    readonly int _head;
    readonly MatrixTransform3D _nose = new(Nothing);
    NVector3? _noseFrom, _noseTo;

    /// <summary>The head bone's own axis that points the way the face looks; when set, a short red stick from the head shows where it
    /// looks (a head turn is otherwise invisible on bare bones). <see cref="FaceAxis"/> finds it.</summary>
    public NVector3? NoseAxis { get; set; }

    /// <summary>Of the head's six local axes, the one nearest the character's forward (+y relative to the mover) in the given model pose.</summary>
    public static NVector3 FaceAxis(System.Numerics.Quaternion headModel)
    {
        NVector3[] axes = [NVector3.UnitX, -NVector3.UnitX, NVector3.UnitY, -NVector3.UnitY, NVector3.UnitZ, -NVector3.UnitZ];
        return axes.MaxBy(a => NVector3.Transform(a, headModel).Y);
    }

    /// <summary>The last pose drawn as a flat picture: the sticks as lines and the joints as dots, each place moved by
    /// <paramref name="offset"/> and projected by <paramref name="project"/> (null for a point behind the camera). Pictures taken with the
    /// window off-screen use it, as a capture there can't see the 3D view.</summary>
    public void Sketch(DrawingContext dc, Func<NVector3, Point?> project, NVector3 offset)
    {
        var bones = _skeleton.Bones;
        byte alpha = _ghost ? (byte)0x70 : (byte)0xFF;
        Brush B(Color c) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        var body = new Pen(B(BodyStickColour), 3);
        var addOn = new Pen(B(Color.FromRgb(0xF5, 0xB0, 0x41)), 2);
        var joint = B(BodyJointColour);
        var addOnJoint = B(Color.FromRgb(0xE8, 0x6A, 0x10));
        // the feet's marks under the bones
        int k = 0;
        foreach (var side in _feet)
            foreach (int b in side)
            {
                int state = k < _footState.Length ? _footState[k] : 0;
                k++;
                if (state == 0 || b < 0 || !_finite[b] || project(_at[b] + offset) is not { } fp) continue;
                var c = state == 2 ? BelowFloorColour : PlantedColour;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x70, c.R, c.G, c.B)), new Pen(new SolidColorBrush(c), 1.5), fp, 7, 7);
            }
        bool Shown(int i) => _finite[i] && (!bones[i].FromAddOn || (i < _addOnShown.Length && _addOnShown[i]));
        for (int i = 0; i < bones.Count; i++)
        {
            if (_stick[i] is null || !Shown(i) || !Shown(bones[i].ParentIndex)) continue;
            if (project(_at[bones[i].ParentIndex] + offset) is { } a && project(_at[i] + offset) is { } b) dc.DrawLine(bones[i].FromAddOn ? addOn : body, a, b);
        }
        for (int i = 0; i < bones.Count; i++)
        {
            if (!Shown(i) || bones[i].IsRoot) continue;
            if (project(_at[i] + offset) is { } p) dc.DrawEllipse(bones[i].FromAddOn ? addOnJoint : joint, null, p, 2.5, 2.5);
        }
        if (_noseFrom is { } nf && _noseTo is { } nt && project(nf + offset) is { } n0 && project(nt + offset) is { } n1)
            dc.DrawLine(new Pen(B(Color.FromRgb(0xE0, 0x50, 0x50)), 3), n0, n1);
    }

    /// <summary>Draws a pose: <paramref name="model"/> by bone index, relative to the mover; with <paramref name="applyMover"/>, each
    /// bone is placed in the clip's world by <paramref name="mover"/>, else the clip plays in place. A bone whose place isn't finite
    /// numbers (damaged keys) is drawn as nothing, with its stick and its children's sticks; <see cref="NotDrawn"/> counts them.</summary>
    public void Update(ReadOnlySpan<Transform> model, Transform mover, bool applyMover)
    {
        var m = applyMover ? mover : Transform.Identity;
        int bad = 0;
        for (int i = 0; i < _at.Length; i++)
        {
            _at[i] = m.Apply(model[i].Position);
            _worldZ[i] = applyMover ? _at[i].Z : mover.Apply(model[i].Position).Z;
            _finite[i] = float.IsFinite(_at[i].X) && float.IsFinite(_at[i].Y) && float.IsFinite(_at[i].Z);
            if (!_finite[i]) bad++;
        }
        NotDrawn = bad;
        for (int i = 0; i < _at.Length; i++)
        {
            var p = _at[i];
            double r = _jointRadius[i];
            _joint[i].Matrix = _finite[i] ? new Matrix3D(r, 0, 0, 0, 0, r, 0, 0, 0, 0, r, 0, p.X, p.Y, p.Z, 1) : Nothing;
            if (_stick[i] is not { } st) continue;
            int parent = _skeleton.Bones[i].ParentIndex;
            st.Matrix = _finite[i] && _finite[parent] ? StickMatrix(_at[parent], p, _stickRadius[i]) : Nothing;
        }
        // the head's look: 12 cm along its face axis, from 6 cm up the head
        _noseFrom = _noseTo = null;
        if (NoseAxis is { } na && _head >= 0 && _finite[_head])
        {
            var hq = (m * model[_head]).Rotation;
            var up = NVector3.Transform(NVector3.UnitX, hq);
            var from = _at[_head] + up * 0.06f;
            var to = from + NVector3.Transform(na, hq) * 0.12f;
            if (float.IsFinite(to.X + to.Y + to.Z)) { _noseFrom = from; _noseTo = to; }
        }
        _nose.Matrix = _noseFrom is { } a0 && _noseTo is { } b0 ? StickMatrix(a0, b0, 0.008 * (_ghost ? GhostThickness : 1)) : Nothing;
        // the arrow lies on the floor under the mover, turned by its heading only
        var fwd = NVector3.Transform(NVector3.UnitY, m.Rotation);
        double h = Math.Atan2(fwd.Y, fwd.X) - Math.PI / 2;
        double c = Math.Cos(h), s = Math.Sin(h);
        bool arrowOk = double.IsFinite(h) && float.IsFinite(m.Position.X) && float.IsFinite(m.Position.Y);
        _arrow.Matrix = arrowOk ? new Matrix3D(c, s, 0, 0, -s, c, 0, 0, 0, 0, 1, 0, m.Position.X, m.Position.Y, _ghost ? -GhostArrowDrop : 0, 1) : Nothing;
        // the camera follows the hips only to a finite place
        int centre = _hips >= 0 ? _hips : 0;
        if (centre < _at.Length && _finite[centre]) Centre = _at[centre];
    }

    /// <summary>The unit stick (radius 1, from z 0 to z 1) laid from <paramref name="a"/> to <paramref name="b"/>: rows are the images of
    /// x, y and z, then the offset (WPF multiplies row vectors).</summary>
    static Matrix3D StickMatrix(NVector3 a, NVector3 b, double r)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return new Matrix3D(1e-5, 0, 0, 0, 0, 1e-5, 0, 0, 0, 0, 1e-5, 0, a.X, a.Y, a.Z, 1);
        var z = d / len;
        var u = NVector3.Normalize(NVector3.Cross(z, MathF.Abs(z.Z) < 0.9f ? NVector3.UnitZ : NVector3.UnitX));
        var v = NVector3.Cross(z, u);
        return new Matrix3D(u.X * r, u.Y * r, u.Z * r, 0, v.X * r, v.Y * r, v.Z * r, 0, d.X, d.Y, d.Z, 0, a.X, a.Y, a.Z, 1);
    }

    /// <summary>A pale, see-through material: the colour at <paramref name="alpha"/>, lit by its own glow so it reads on the dark view.</summary>
    static Material Ghostly(Color c, byte alpha = 0x60)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B))));
        g.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromArgb(alpha, (byte)(c.R / 3), (byte)(c.G / 3), (byte)(c.B / 3)))));
        g.Freeze();
        return g;
    }

    static Material Shiny(Color c)
    {
        var g = new MaterialGroup();
        g.Children.Add(new DiffuseMaterial(new SolidColorBrush(c)));
        g.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)), 30));
        g.Freeze();
        return g;
    }

    static MeshGeometry3D Cylinder(int sides)
    {
        var m = new MeshGeometry3D();
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides, x = Math.Cos(a), y = Math.Sin(a);
            m.Positions.Add(new Point3D(x, y, 0)); m.Positions.Add(new Point3D(x, y, 1));
            m.Normals.Add(new Vector3D(x, y, 0)); m.Normals.Add(new Vector3D(x, y, 0));
        }
        for (int i = 0; i < sides; i++)
        {
            int a = 2 * i, b = a + 1, c = 2 * ((i + 1) % sides), d = c + 1;
            m.TriangleIndices.Add(a); m.TriangleIndices.Add(c); m.TriangleIndices.Add(d);
            m.TriangleIndices.Add(a); m.TriangleIndices.Add(d); m.TriangleIndices.Add(b);
        }
        m.Freeze();
        return m;
    }

    static MeshGeometry3D Sphere(int segments, int rings)
    {
        var m = new MeshGeometry3D();
        for (int r = 0; r <= rings; r++)
        {
            double phi = Math.PI * r / rings, z = Math.Cos(phi), s = Math.Sin(phi);
            for (int k = 0; k <= segments; k++)
            {
                double th = 2 * Math.PI * k / segments;
                var p = new Point3D(s * Math.Cos(th), s * Math.Sin(th), z);
                m.Positions.Add(p);
                m.Normals.Add((Vector3D)p);
            }
        }
        for (int r = 0; r < rings; r++)
            for (int k = 0; k < segments; k++)
            {
                int i0 = r * (segments + 1) + k, i1 = i0 + 1, i2 = i0 + segments + 1, i3 = i2 + 1;
                m.TriangleIndices.Add(i0); m.TriangleIndices.Add(i2); m.TriangleIndices.Add(i1);
                m.TriangleIndices.Add(i1); m.TriangleIndices.Add(i2); m.TriangleIndices.Add(i3);
            }
        m.Freeze();
        return m;
    }

    /// <summary>A flat arrow on the floor pointing +y (the character's forward), 30 cm long.</summary>
    static MeshGeometry3D ArrowMesh()
    {
        var m = new MeshGeometry3D();
        const double z = 0.004;
        foreach (var (x, y) in new[] { (0.0, 0.30), (-0.10, 0.12), (0.10, 0.12), (-0.035, 0.12), (0.035, 0.12), (-0.035, -0.08), (0.035, -0.08) })
            m.Positions.Add(new Point3D(x, y, z));
        foreach (int i in new[] { 0, 1, 2, 3, 5, 6, 3, 6, 4 }) m.TriangleIndices.Add(i);
        m.Freeze();
        return m;
    }

    /// <summary>The floor at z = 0: a plain square of 2 x <paramref name="half"/> metres around the origin with a line every metre, and
    /// half-metre lines near the middle (far off they would only flicker). Lines are thin flat bands, seen from above and below.</summary>
    public static Model3DGroup FloorGrid(int half = 12, int minorHalf = 4)
    {
        var plane = new MeshGeometry3D();
        Band(plane, new Point3D(-half, 0, -0.003), new Point3D(half, 0, -0.003), 2 * half);
        var minor = new MeshGeometry3D();
        var major = new MeshGeometry3D();
        for (int i = -half; i <= half; i++)
        {
            Band(major, new Point3D(-half, i, 0), new Point3D(half, i, 0), 0.012);
            Band(major, new Point3D(i, -half, 0), new Point3D(i, half, 0), 0.012);
        }
        for (int i = -minorHalf; i < minorHalf; i++)
        {
            double at = i + 0.5;
            Band(minor, new Point3D(-minorHalf, at, 0), new Point3D(minorHalf, at, 0), 0.006);
            Band(minor, new Point3D(at, -minorHalf, 0), new Point3D(at, minorHalf, 0), 0.006);
        }
        plane.Freeze();
        minor.Freeze();
        major.Freeze();
        var g = new Model3DGroup();
        g.Children.Add(Flat(plane, Color.FromRgb(0x36, 0x39, 0x3F)));
        g.Children.Add(Flat(minor, Color.FromRgb(0x55, 0x59, 0x60)));
        g.Children.Add(Flat(major, Color.FromRgb(0x78, 0x7C, 0x84)));
        return g;
    }

    /// <summary>Half-metre marks of the world's axes at the origin, on the floor: x red, y green.</summary>
    public static Model3DGroup OriginAxes()
    {
        var x = new MeshGeometry3D();
        var y = new MeshGeometry3D();
        Band(x, new Point3D(0, 0, 0.001), new Point3D(0.5, 0, 0.001), 0.02);
        Band(y, new Point3D(0, 0, 0.001), new Point3D(0, 0.5, 0.001), 0.02);
        x.Freeze();
        y.Freeze();
        var g = new Model3DGroup();
        g.Children.Add(Flat(x, Color.FromRgb(0xD9, 0x48, 0x48)));
        g.Children.Add(Flat(y, Color.FromRgb(0x48, 0xC0, 0x58)));
        return g;
    }

    /// <summary>A band on the floor from <paramref name="a"/> to <paramref name="b"/>, <paramref name="w"/> wide.</summary>
    static void Band(MeshGeometry3D m, Point3D a, Point3D b, double w)
    {
        var d = b - a;
        var n = new Vector3D(-d.Y, d.X, 0);
        n.Normalize();
        n *= w / 2;
        int k = m.Positions.Count;
        m.Positions.Add(a + n); m.Positions.Add(a - n); m.Positions.Add(b + n); m.Positions.Add(b - n);
        foreach (int i in new[] { 0, 1, 2, 1, 3, 2 }) m.TriangleIndices.Add(k + i);
    }

    static GeometryModel3D Flat(MeshGeometry3D m, Color c)
    {
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Colors.Black)));
        mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(c)));
        mat.Freeze();
        return new GeometryModel3D(m, mat) { BackMaterial = mat };
    }

    /// <summary>A flat band on the floor along <paramref name="points"/> (the mover's path), or null when the path doesn't go anywhere.
    /// Points that aren't finite numbers are left out.</summary>
    public static GeometryModel3D? PathModel(IReadOnlyList<NVector3> points, double width = 0.025)
    {
        var pts = new List<NVector3>();
        foreach (var p in points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) continue;
            var f = new NVector3(p.X, p.Y, 0);
            if (pts.Count == 0 || NVector3.Distance(pts[^1], f) > 1e-3f) pts.Add(f);
        }
        if (pts.Count < 2) return null;
        var m = new MeshGeometry3D();
        for (int k = 0; k < pts.Count; k++)
        {
            var t = pts[Math.Min(k + 1, pts.Count - 1)] - pts[Math.Max(k - 1, 0)];
            var n = t.Length() > 1e-6f ? new NVector3(-t.Y, t.X, 0) / t.Length() * (float)(width / 2) : NVector3.Zero;
            m.Positions.Add(new Point3D(pts[k].X + n.X, pts[k].Y + n.Y, 0.003));
            m.Positions.Add(new Point3D(pts[k].X - n.X, pts[k].Y - n.Y, 0.003));
            if (k == 0) continue;
            int a = 2 * (k - 1);
            m.TriangleIndices.Add(a); m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(a + 2);
            m.TriangleIndices.Add(a + 1); m.TriangleIndices.Add(a + 3); m.TriangleIndices.Add(a + 2);
        }
        m.Freeze();
        var mat = new MaterialGroup();
        mat.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x30))));
        mat.Children.Add(new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(0x70, 0x60, 0x10))));
        mat.Freeze();
        return new GeometryModel3D(m, mat) { BackMaterial = mat };
    }
}
