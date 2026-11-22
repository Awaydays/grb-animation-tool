using System.Buffers.Binary;
using System.Numerics;

namespace GrbAnimTool.Core.Clips;

/// <summary>A foot plant key: how long the foot stays planted from the key's tick, and where (metres; x lateral, y forward, z height).
/// The point is not relative to the moving body: in clips that travel it stands along the clip's path, metres from the origin.</summary>
public readonly record struct FootPlant(int Tick, int LengthTicks, Vector3 Point);

/// <summary>Decodes a curve's keys: rotations, positions, foot plants, and key times. Every read is checked against the curve's own extent:
/// a key outside the curve gives null.</summary>
public static class CurveDecoder
{
    const float Off = -0.70710677f;

    static bool Fits(Curve c, int key) => key >= 0 && key < c.Keys && c.ValuesAt >= 0 && c.ValuesAt + (long)(key + 1) * c.ValueBytes <= c.Source.Length;

    /// <summary>One value about the bone's own Z (tags 0x12 to 0x17): the top bit says which of z and w is the stored one; the other is
    /// rebuilt positive.</summary>
    static Quaternion Hinge(float f, bool swapped)
    {
        float r = MathF.Sqrt(MathF.Max(0, 1 - f * f));
        return swapped ? new Quaternion(0, 0, r, f) : new Quaternion(0, 0, f, r);
    }

    /// <summary>A rotation key, normalised, by the game's forms: 2 bytes (4 bits a component), 4 (10 bits), 6 (15 bits), 8 (20 bits), 12 (three
    /// floats, their low bits the selector), 16 (four floats); all but the last are "smallest three", the dropped component rebuilt positive
    /// (negative in the 2-byte form when its bit 13 is set). Tags 0x12 to 0x17 hold one value about Z. Null for a form not read (3 bytes),
    /// for a curve that isn't a rotation, or for a key outside the curve.</summary>
    public static Quaternion? KeyRot(Curve c, int key = 0) => TryKeyRot(c, key, out var q, out _) ? q : null;

    /// <summary><see cref="KeyRot"/>, also giving <paramref name="stored"/>: for the smallest-three forms the stored triple's squared length
    /// (at most 1 for a valid key), for a four-float key its squared length before normalising, for the one-value form the value squared.</summary>
    public static bool TryKeyRot(Curve c, int key, out Quaternion q, out float stored)
    {
        q = Quaternion.Identity; stored = float.NaN;
        if (!CurveTags.IsRotation(c.Tag) || !Fits(c, key)) return false;
        var b = c.Source;
        int at = c.ValuesAt + key * c.ValueBytes;
        if (c.Tag is >= 0x12 and <= 0x14)
        {
            float f = (b[at] & 0x7F) * 0.011223917f + Off;
            stored = f * f;
            q = Hinge(f, (b[at] & 0x80) != 0);
            return true;
        }
        if (c.Tag is >= 0x15 and <= 0x17)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
            float f = (v & 0x7FFF) * 4.3161006e-05f + Off;
            stored = f * f;
            q = Hinge(f, (v & 0x8000) != 0);
            return true;
        }
        float x, y, z; int sel; bool neg = false;
        switch (c.ValueBytes)
        {
            case 2:
                {
                    uint v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
                    x = ((v >> 8) & 0xF) * 0.101015255f + Off; y = ((v >> 4) & 0xF) * 0.101015255f + Off; z = (v & 0xF) * 0.101015255f + Off;
                    sel = (int)(v >> 14) & 3; neg = (v & 0x2000) != 0;
                    break;
                }
            case 4:
                {
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
                    x = ((v >> 20) & 0x3FF) * 0.0013837706f + Off; y = ((v >> 10) & 0x3FF) * 0.0013837706f + Off; z = (v & 0x3FF) * 0.0013837706f + Off;
                    sel = (int)(v >> 30) & 3;
                    break;
                }
            case 6:
                {
                    uint u0 = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)), u1 = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 2)), u2 = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at + 4));
                    x = (u0 & 0x7FFF) * 4.3161006e-05f + Off; y = (u1 & 0x7FFF) * 4.3161006e-05f + Off; z = (u2 & 0x7FFF) * 4.3161006e-05f + Off;
                    sel = (int)(u0 >> 15) | ((int)(u1 >> 15) << 1);
                    break;
                }
            case 8:
                {
                    ulong u = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(at));
                    x = ((u >> 8) & 0xFFFFF) * 1.3487017e-06f + Off;
                    y = (((u & 0xFF) << 12) | ((u >> 52) & 0xFFF)) * 1.3487017e-06f + Off;
                    z = ((u >> 32) & 0xFFFFF) * 1.3487017e-06f + Off;
                    sel = (int)(u >> 30) & 3;
                    break;
                }
            case 12:
                {
                    uint r0 = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)), r1 = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 4));
                    x = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)); y = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)); z = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8));
                    sel = (int)(r0 & 1) | ((int)(r1 & 1) << 1);
                    break;
                }
            case 16:
                {
                    var raw = new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 12)));
                    stored = raw.LengthSquared();
                    q = Quaternion.Normalize(raw);
                    return true;
                }
            default: return false;
        }
        stored = x * x + y * y + z * z;
        float r2 = 1 - stored;
        float r = r2 > 0 ? MathF.Sqrt(r2) : 0;
        if (neg) r = -r;
        var s = sel switch { 0 => new Quaternion(r, x, y, z), 1 => new Quaternion(x, r, y, z), 2 => new Quaternion(x, y, r, z), _ => new Quaternion(x, y, z, r) };
        q = Quaternion.Normalize(s);
        return true;
    }

    /// <summary>A position key (tags 0x1B to 0x1D: three floats, metres). Null for any other tag or a key outside the curve.</summary>
    public static Vector3? KeyPos(Curve c, int key = 0)
    {
        if (c.Tag is not (>= 0x1B and <= 0x1D) || !Fits(c, key)) return null;
        int at = c.ValuesAt + key * c.ValueBytes;
        var s = c.Source.AsSpan(at);
        return new Vector3(BinaryPrimitives.ReadSingleLittleEndian(s), BinaryPrimitives.ReadSingleLittleEndian(s[4..]), BinaryPrimitives.ReadSingleLittleEndian(s[8..]));
    }

    /// <summary>A foot plant key (tags 0x42 to 0x44, four signed 16-bit values): the first is the plant's length in ticks; the second, third
    /// and fourth are the height, y and x in mm. Null for any other tag or a key outside the curve.</summary>
    public static FootPlant? Plant(Curve c, int key)
    {
        if (c.Tag is not (0x42 or 0x43 or 0x44) || !Fits(c, key)) return null;
        var s = c.Source.AsSpan(c.ValuesAt + key * 8);
        short len = BinaryPrimitives.ReadInt16LittleEndian(s), h = BinaryPrimitives.ReadInt16LittleEndian(s[2..]),
            y = BinaryPrimitives.ReadInt16LittleEndian(s[4..]), x = BinaryPrimitives.ReadInt16LittleEndian(s[6..]);
        return new FootPlant(KeyTick(c, key), len, new Vector3(x, y, h) * 0.001f);
    }

    /// <summary>A key's time in ticks, by the same rule as <see cref="KeyTicks"/>. In the byte and u16 time forms the first key's time isn't
    /// stored (tick 0); a bitmap's keys, the first included, stand at its set bits, shifted (on the game's files bit 0 is always set). A key
    /// the times don't hold gives 0.</summary>
    public static int KeyTick(Curve c, int key)
    {
        if (key < 0 || key >= c.Keys) return 0;
        var s = c.Source;
        switch (c.Tag % 3)
        {
            case 0: return key > 0 && c.TimesAt + key - 1 < s.Length ? s[c.TimesAt + key - 1] : 0;
            case 1: return key > 0 && c.TimesAt + key * 2 <= s.Length ? BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(c.TimesAt + (key - 1) * 2)) : 0;
            default:
                if (c.Keys <= 1) return 0;   // as KeyTicks: a single key stands at tick 0
                int seen = -1;
                for (int bit = 0; bit < c.BitmapBytes * 8 && c.TimesAt + 1 + bit / 8 < s.Length; bit++)
                    if ((s[c.TimesAt + 1 + bit / 8] & (1 << (bit % 8))) != 0 && ++seen == key) return bit << c.BitmapShift;
                return 0;
        }
    }

    /// <summary>All of a curve's key times at once (a bitmap curve is walked once instead of once a key). At least one entry.</summary>
    public static int[] KeyTicks(Curve c)
    {
        var t = new int[Math.Max(1, (int)Math.Min(c.Keys, int.MaxValue))];
        if (c.Keys <= 1) return t;
        var s = c.Source;
        switch (c.Tag % 3)
        {
            case 0: for (int k = 1; k < t.Length && c.TimesAt + k - 1 < s.Length; k++) t[k] = s[c.TimesAt + k - 1]; break;
            case 1: for (int k = 1; k < t.Length && c.TimesAt + k * 2 <= s.Length; k++) t[k] = BinaryPrimitives.ReadUInt16LittleEndian(s.AsSpan(c.TimesAt + (k - 1) * 2)); break;
            default:
                int seen = -1;
                for (int bit = 0; bit < c.BitmapBytes * 8 && seen + 1 < t.Length && c.TimesAt + 1 + bit / 8 < s.Length; bit++)
                    if ((s[c.TimesAt + 1 + bit / 8] & (1 << (bit % 8))) != 0) t[++seen] = bit << c.BitmapShift;
                break;
        }
        return t;
    }
}
