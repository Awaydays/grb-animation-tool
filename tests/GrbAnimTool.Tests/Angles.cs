using System.Numerics;

namespace GrbAnimTool.Tests;

/// <summary>The angle between two rotations, in degrees.</summary>
public static class Angles
{
    /// <summary>Computed in double from the components (each normalised, the sign of b matched to a's), as 4 atan2(|a - b|, |a + b|):
    /// exact down to a float's last bit.</summary>
    public static double Deg(Quaternion a, Quaternion b)
    {
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W, bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
        double la = Math.Sqrt(ax * ax + ay * ay + az * az + aw * aw), lb = Math.Sqrt(bx * bx + by * by + bz * bz + bw * bw);
        ax /= la; ay /= la; az /= la; aw /= la; bx /= lb; by /= lb; bz /= lb; bw /= lb;
        if (ax * bx + ay * by + az * bz + aw * bw < 0) { bx = -bx; by = -by; bz = -bz; bw = -bw; }
        double d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz) + (aw - bw) * (aw - bw));
        double s = Math.Sqrt((ax + bx) * (ax + bx) + (ay + by) * (ay + by) + (az + bz) * (az + bz) + (aw + bw) * (aw + bw));
        return 4 * Math.Atan2(d, s) * 180 / Math.PI;
    }

    /// <summary>The same angle as 2 acos(|dot|) with the dot product in float: its smallest step above 0 is 0.0396 deg (a dot one float step
    /// below 1), so small differences come out as multiples of that grain.</summary>
    public static double FloatDotDeg(Quaternion a, Quaternion b) =>
        2 * Math.Acos(Math.Min(1, Math.Abs((double)Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))))) * 180 / Math.PI;
}
