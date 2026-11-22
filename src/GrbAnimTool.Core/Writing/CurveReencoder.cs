using System.Numerics;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Writing;

/// <summary>A curve decoded and written again by the writers, in its own tag and time form: the path every edit takes for a curve it
/// changes. Tags with a writer: 0x0C and 0x0D (6-byte rotations), 0x18 and 0x19 (12-byte rotations), 0x1B and 0x1C (positions), 0x27 and
/// 0x28 (bytes), 0x42 and 0x43 (foot plants). The bitmap time forms (0x0E, 0x1A, 0x1D, 0x29, 0x44) and every other value form have none.</summary>
public static class CurveReencoder
{
    /// <summary>Whether <see cref="Reencode"/> writes curves of this tag.</summary>
    public static bool HasWriter(byte tag) => tag is 0x0C or 0x0D or 0x18 or 0x19 or 0x1B or 0x1C or 0x27 or 0x28 or 0x42 or 0x43;

    /// <summary>The curve's keys decoded and written again, or null when its tag has no writer (or it is a reference). A key the decoder
    /// can't read, or a value the writer refuses, throws <see cref="ArgumentException"/>.</summary>
    public static byte[]? Reencode(Curve c)
    {
        if (c.IsReference || !HasWriter(c.Tag)) return null;
        var ticks = CurveDecoder.KeyTicks(c);
        if (ticks.Length != c.Keys) throw new ArgumentException($"tag 0x{c.Tag:X2}: {ticks.Length} times for {c.Keys} keys");
        bool wide = c.Tag % 3 == 1;
        int k = ticks.Length;
        switch (c.Tag)
        {
            case 0x0C or 0x0D or 0x18 or 0x19:
                {
                    var q = new Quaternion[k];
                    for (int i = 0; i < k; i++) q[i] = CurveDecoder.KeyRot(c, i) ?? throw new ArgumentException($"tag 0x{c.Tag:X2}: key {i} can't be read");
                    return c.Tag <= 0x0D ? CurveWriter.RotCurve(ticks, q, wide) : CurveWriter.RotCurve12(ticks, q, wide);
                }
            case 0x1B or 0x1C:
                {
                    var p = new Vector3[k];
                    for (int i = 0; i < k; i++) p[i] = CurveDecoder.KeyPos(c, i) ?? throw new ArgumentException($"tag 0x{c.Tag:X2}: key {i} can't be read");
                    return CurveWriter.PosCurve(ticks, p, wide);
                }
            case 0x27 or 0x28:
                {
                    var v = new byte[k];
                    for (int i = 0; i < k; i++) v[i] = KeyByte(c, i) ?? throw new ArgumentException($"tag 0x{c.Tag:X2}: key {i} can't be read");
                    return CurveWriter.ByteCurve(ticks, v, wide);
                }
            default:
                {
                    var f = new FootPlant[k];
                    for (int i = 0; i < k; i++) f[i] = CurveDecoder.Plant(c, i) ?? throw new ArgumentException($"tag 0x{c.Tag:X2}: key {i} can't be read");
                    return CurveWriter.PlantCurve(f, wide);
                }
        }
    }

    /// <summary>A byte track's key (form 13, tags 0x27 to 0x29: one byte a key); null for another form or a key outside the curve.</summary>
    public static byte? KeyByte(Curve c, int key)
    {
        if (c.Form != 13 || key < 0 || key >= c.Keys || c.ValuesAt < 0 || c.ValuesAt + (long)key + 1 > c.Source.Length) return null;
        return c.Source[c.ValuesAt + key];
    }
}
