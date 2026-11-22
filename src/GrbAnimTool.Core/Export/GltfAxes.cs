using System.Numerics;
using GrbAnimTool.Core.Skeletons;

namespace GrbAnimTool.Core.Export;

/// <summary>The hand-over between the game's model space and glTF's.
/// <para>The game's model space has x the character's left-right (its left at -x), y forward and z up, in metres. glTF is y up, so every
/// vector turns by (x, y, z) to (x, z, -y): a proper rotation (-90 deg about x), so a rotation's vector part turns the same way and its w
/// stays, and composing transforms commutes with the turn. Blender's importer turns glTF's axes back (x, -z, y), so Blender's world is the
/// game's model space, 1 unit a metre.</para></summary>
public static class GltfAxes
{
    public static Vector3 ToGltf(Vector3 p) => new(p.X, p.Z, -p.Y);
    public static Quaternion ToGltf(Quaternion q) => new(q.X, q.Z, -q.Y, q.W);
    public static Transform ToGltf(Transform t) => new(ToGltf(t.Position), ToGltf(t.Rotation));

    public static Vector3 FromGltf(Vector3 p) => new(p.X, -p.Z, p.Y);
    public static Quaternion FromGltf(Quaternion q) => new(q.X, -q.Z, q.Y, q.W);

    /// <summary>The inverse of a rigid transform.</summary>
    public static Transform Inverse(Transform t)
    {
        var r = Quaternion.Conjugate(Quaternion.Normalize(t.Rotation));
        return new Transform(-Vector3.Transform(t.Position, r), r);
    }

    /// <summary>The angle between two rotations in degrees, by the vector part of their difference in doubles (2 acos of the dot can't
    /// see below about 0.05 deg in floats). The sign of a quaternion doesn't count.</summary>
    public static double AngleDeg(Quaternion a, Quaternion b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W, bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
        double na = Math.Sqrt(ax * ax + ay * ay + az * az + aw * aw), nb = Math.Sqrt(bx * bx + by * by + bz * bz + bw * bw);
        if (na == 0 || nb == 0 || !double.IsFinite(na + nb)) return 180;
        ax /= na; ay /= na; az /= na; aw /= na; bx /= nb; by /= nb; bz /= nb; bw /= nb;
        // conj(a) * b
        double w = aw * bw + ax * bx + ay * by + az * bz;
        double x = aw * bx - ax * bw - ay * bz + az * by;
        double y = aw * by + ax * bz - ay * bw - az * bx;
        double z = aw * bz - ax * by + ay * bx - az * bw;
        return 2 * Math.Atan2(Math.Sqrt(x * x + y * y + z * z), Math.Abs(w)) * 180 / Math.PI;
    }
}
