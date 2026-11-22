using System.Numerics;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Edits;

/// <summary>One curve's value at any time, by the same blend as <see cref="ClipSampler"/>: the last key at or before the time and the next,
/// slerp for rotations and lerp for positions; before the first key or after the last, the nearest key holds.</summary>
public static class CurveValues
{
    /// <summary>The index of the last key at or before <paramref name="tick"/> (the last of several keys at one tick), 0 before the first.</summary>
    public static int KeyAtOrBefore(int[] ticks, double tick)
    {
        int lo = 0, hi = ticks.Length - 1, k = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (ticks[mid] <= tick + 1e-6) { k = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return k;
    }

    static (int K, float F) Span(int[] t, double tick)
    {
        int k = KeyAtOrBefore(t, tick);
        float f = k + 1 < t.Length && t[k + 1] > t[k] ? (float)Math.Clamp((tick - t[k]) / (t[k + 1] - t[k]), 0, 1) : 0;
        return (k, f);
    }

    /// <summary>A rotation curve's value at <paramref name="tick"/>; <paramref name="ticks"/> are its key times.</summary>
    public static Quaternion Rot(Curve c, int[] ticks, double tick)
    {
        var (k, f) = Span(ticks, tick);
        var a = CurveDecoder.KeyRot(c, k) ?? throw new EditRefusedException(EditRefusal.Unreadable, $"a rotation key (tag 0x{c.Tag:X2}, key {k}) doesn't decode");
        if (f > 0 && CurveDecoder.KeyRot(c, k + 1) is { } b) a = Quaternion.Slerp(a, b, f);
        return Quaternion.Normalize(a);
    }

    /// <summary>A position curve's value at <paramref name="tick"/>.</summary>
    public static Vector3 Pos(Curve c, int[] ticks, double tick)
    {
        var (k, f) = Span(ticks, tick);
        var a = CurveDecoder.KeyPos(c, k) ?? throw new EditRefusedException(EditRefusal.Unreadable, $"a position key (tag 0x{c.Tag:X2}, key {k}) doesn't decode");
        if (f > 0 && CurveDecoder.KeyPos(c, k + 1) is { } b) a = Vector3.Lerp(a, b, f);
        return a;
    }

    /// <summary>A rotation curve's key <paramref name="k"/> itself, normalised: of two keys at one tick (a step) each has its own value.</summary>
    public static Quaternion KeyRot(Curve c, int k) => Quaternion.Normalize(CurveDecoder.KeyRot(c, k)
        ?? throw new EditRefusedException(EditRefusal.Unreadable, $"a rotation key (tag 0x{c.Tag:X2}, key {k}) doesn't decode"));

    /// <summary>A position curve's key <paramref name="k"/> itself.</summary>
    public static Vector3 KeyPos(Curve c, int k) => CurveDecoder.KeyPos(c, k)
        ?? throw new EditRefusedException(EditRefusal.Unreadable, $"a position key (tag 0x{c.Tag:X2}, key {k}) doesn't decode");

    /// <summary>The angle between two rotations in degrees, either sign of the second.</summary>
    public static double Deg(Quaternion a, Quaternion b)
    {
        // in double throughout, as 4 atan2(|a - b|, |a + b|) with b's sign matched: exact for small angles, where an acos of the dot
        // product loses them to rounding
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W, bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
        double la = Math.Sqrt(ax * ax + ay * ay + az * az + aw * aw), lb = Math.Sqrt(bx * bx + by * by + bz * bz + bw * bw);
        if (la == 0 || lb == 0) return double.NaN;
        ax /= la; ay /= la; az /= la; aw /= la; bx /= lb; by /= lb; bz /= lb; bw /= lb;
        if (ax * bx + ay * by + az * bz + aw * bw < 0) { bx = -bx; by = -by; bz = -bz; bw = -bw; }
        double d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by) + (az - bz) * (az - bz) + (aw - bw) * (aw - bw));
        double s = Math.Sqrt((ax + bx) * (ax + bx) + (ay + by) * (ay + by) + (az + bz) * (az + bz) + (aw + bw) * (aw + bw));
        return 4 * Math.Atan2(d, s) * 180 / Math.PI;
    }

    /// <summary>Whether a rotation curve already holds these keys: the same times, and every value within <paramref name="deg"/>.</summary>
    public static bool SameRot(Curve c, IReadOnlyList<int> ticks, IReadOnlyList<Quaternion> q, double deg = 1e-3)
    {
        if (c.Keys != ticks.Count) return false;
        var t = CurveDecoder.KeyTicks(c);
        for (int i = 0; i < ticks.Count; i++)
            if (t[i] != ticks[i] || CurveDecoder.KeyRot(c, i) is not { } v || Deg(v, q[i]) > deg) return false;
        return true;
    }

    /// <summary>Whether a position curve already holds these keys: the same times, and every value within <paramref name="m"/> metres.</summary>
    public static bool SamePos(Curve c, IReadOnlyList<int> ticks, IReadOnlyList<Vector3> p, double m = 1e-6)
    {
        if (c.Keys != ticks.Count) return false;
        var t = CurveDecoder.KeyTicks(c);
        for (int i = 0; i < ticks.Count; i++)
            if (t[i] != ticks[i] || CurveDecoder.KeyPos(c, i) is not { } v || (v - p[i]).Length() > m) return false;
        return true;
    }

    /// <summary>A rotation curve in the form a track keeps: the 12-byte form where the track's curve was in it (tags 0x18 to 0x1A), else the
    /// 6-byte form (0x0C, 0x0D), every other rotation form having no writer.</summary>
    public static byte[] WriteRot(Curve was, IReadOnlyList<int> ticks, IReadOnlyList<Quaternion> q) =>
        was.Tag is >= 0x18 and <= 0x1A ? Writing.CurveWriter.RotCurve12(ticks, q) : Writing.CurveWriter.RotCurve(ticks, q);
}
