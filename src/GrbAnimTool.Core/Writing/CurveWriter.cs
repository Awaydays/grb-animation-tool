using System.Buffers.Binary;
using System.Numerics;
using GrbAnimTool.Core.Clips;

namespace GrbAnimTool.Core.Writing;

/// <summary>Writes inline curves in the game's layout: the tag, a u32 size in memory, a u32 key count, the key times after the first, then
/// the values. The times stand one byte a key (the base tag) or, when a time passes 255, two bytes a key (the base tag + 1); the first key's
/// time is not stored (tick 0). The size in memory is 2 B, 2 B more from 256 keys on, plus the times, padded to the value's alignment, plus
/// the values: the rule <see cref="CurveWalker.SizeFits"/> checks on every curve read. A bad input (no keys, times that fall, values of
/// unequal sizes, a value out of its form's range) is refused with an <see cref="ArgumentException"/>; nothing is written then.</summary>
public static class CurveWriter
{
    /// <summary>The most keys <see cref="Curve"/> writes; <see cref="CurveN"/> writes more.</summary>
    public const int MaxShortKeys = 255;

    /// <summary>The most keys <see cref="CurveN"/> writes: one a tick over the longest clip a record may state.</summary>
    public const int MaxKeys = (int)(ClipRecord.MaxDuration * ClipRecord.TicksPerSecond) + 1;

    /// <summary>A rotation's base tag in the 6-byte form (0x0C one-byte times, 0x0D two-byte).</summary>
    public const byte Rotation6 = 0x0C;
    /// <summary>A rotation's base tag in the 12-byte form (0x18 one-byte times, 0x19 two-byte).</summary>
    public const byte Rotation12 = 0x18;
    /// <summary>A position's base tag (0x1B one-byte times, 0x1C two-byte).</summary>
    public const byte Position = 0x1B;
    /// <summary>A byte track's base tag (0x27 one-byte times, 0x28 two-byte).</summary>
    public const byte ByteTrack = 0x27;
    /// <summary>Foot plants' base tag (0x42 one-byte times, 0x43 two-byte).</summary>
    public const byte Plants = 0x42;

    static int Align(int v, int a) => (v + a - 1) / a * a;

    /// <summary>A curve of 1 to 255 keys. <paramref name="baseTag"/> is the tag of the one-byte time form; <paramref name="align"/> the value's
    /// alignment in memory (1, 2, 4 or 8). <paramref name="twoByteTimes"/> picks the time form; by default two bytes only when the last time
    /// passes 255. Past 255 keys use <see cref="CurveN"/> (or key the clip by <see cref="KeyingRule.Ticks"/>, which stays within 255).</summary>
    public static byte[] Curve(byte baseTag, int align, IReadOnlyList<int> ticks, IReadOnlyList<byte[]> values, bool? twoByteTimes = null)
    {
        if (ticks.Count > MaxShortKeys) throw new ArgumentException($"{ticks.Count} keys: this form writes 1 to {MaxShortKeys}; use CurveN for more");
        return CurveN(baseTag, align, ticks, values, twoByteTimes);
    }

    /// <summary>The same layout as <see cref="Curve"/> with up to <see cref="MaxKeys"/> keys: from 256 keys on the size in memory holds 2 B
    /// more, as the game's own long curves (up to 1,080 keys in the armed sprint's composite clips) have it.</summary>
    public static byte[] CurveN(byte baseTag, int align, IReadOnlyList<int> ticks, IReadOnlyList<byte[]> values, bool? twoByteTimes = null)
    {
        int k = ticks.Count;
        if (k < 1 || k > MaxKeys) throw new ArgumentException($"{k} keys: 1 to {MaxKeys} are written");
        if (values.Count != k) throw new ArgumentException($"{values.Count} values for {k} keys");
        if (baseTag % 3 != 0 || baseTag + 1 > 0xFD) throw new ArgumentException($"tag 0x{baseTag:X2} is not the one-byte time form of a value form");
        if (align is not (1 or 2 or 4 or 8)) throw new ArgumentException($"alignment {align}: 1, 2, 4 or 8");
        if (ticks[0] != 0) throw new ArgumentException($"the first key stands at tick {ticks[0]}: it must stand at 0 (its time isn't stored)");
        // two keys at one tick are a step the game's own curves hold (a value changing at once); a time that falls is not
        for (int i = 1; i < k; i++) if (ticks[i] < ticks[i - 1]) throw new ArgumentException($"key times must not fall: key {i} at {ticks[i]} after {ticks[i - 1]}");
        int e = values[0]?.Length ?? 0;
        if (e < 1) throw new ArgumentException("a value of no bytes");
        int want = CurveTags.ValueBytes(baseTag);
        if (want >= 0 && want != e) throw new ArgumentException($"tag 0x{baseTag:X2} holds {want}-byte values, not {e}");
        for (int i = 1; i < k; i++) if (values[i]?.Length != e) throw new ArgumentException($"value {i} is {values[i]?.Length ?? 0} B, value 0 is {e} B");
        bool wide = twoByteTimes ?? ticks[k - 1] > 255;
        if (!wide && ticks[k - 1] > 255) throw new ArgumentException($"tick {ticks[k - 1]} needs the two-byte time form");
        if (ticks[k - 1] > ushort.MaxValue) throw new ArgumentException($"tick {ticks[k - 1]} is past the two-byte time form's {ushort.MaxValue}");
        int tw = wide ? 2 : 1, times = (k - 1) * tw;
        var b = new byte[9 + times + k * e];
        b[0] = (byte)(baseTag + (wide ? 1 : 0));
        int head = 2 + (k >= 256 ? 2 : 0) + times;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), (uint)(Align(head, align) + k * e));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), (uint)k);
        for (int i = 1; i < k; i++)
        {
            if (wide) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(9 + (i - 1) * 2), (ushort)ticks[i]);
            else b[9 + i - 1] = (byte)ticks[i];
        }
        for (int i = 0; i < k; i++) values[i].CopyTo(b, 9 + times + i * e);
        return b;
    }

    /// <summary>A rotation in the 6-byte form (tags 0x0C to 0x0E): normalised, the largest component dropped, the other three negated first
    /// when it is negative (the reader rebuilds it positive), each in 15 bits over [-0.7071, 0.7071]; the dropped component's index in the top
    /// bits of the first two u16. A rotation and its negation give the same bytes.</summary>
    public static byte[] Encode6(Quaternion q0)
    {
        var q = Normalised(q0);
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        int sel = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[sel])) sel = i;
        float s = c[sel] < 0 ? -1 : 1;
        var b = new byte[6];
        for (int i = 0, j = 0; i < 4; i++)
        {
            if (i == sel) continue;
            var u = (ushort)Math.Clamp((int)MathF.Round((c[i] * s + 0.70710677f) / 4.3161006e-05f), 0, 0x7FFF);
            if (j < 2) u |= (ushort)(((sel >> j) & 1) << 15);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(j * 2), u);
            j++;
        }
        return b;
    }

    /// <summary>A rotation in the 12-byte form (tags 0x18 to 0x1A): the largest component dropped (on a tie in float the first), the other
    /// three as floats in order, negated first when the dropped one is negative, the dropped component's index in the low bit of the first
    /// float (bit 0 of the index) and of the second (bit 1). <paramref name="normalize"/> false writes the components as given.</summary>
    public static byte[] Encode12(Quaternion q0, bool normalize = true)
    {
        var q = normalize ? Normalised(q0) : q0;
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        int sel = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(c[i]) > MathF.Abs(c[sel])) sel = i;
        float s = c[sel] < 0 ? -1 : 1;
        var b = new byte[12];
        for (int i = 0, j = 0; i < 4; i++)
        {
            if (i == sel) continue;
            uint u = BitConverter.SingleToUInt32Bits(c[i] * s);
            if (j < 2) u = (u & ~1u) | (uint)((sel >> j) & 1);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(j * 4), u);
            j++;
        }
        return b;
    }

    static Quaternion Normalised(Quaternion q)
    {
        float l = q.LengthSquared();
        if (!float.IsFinite(l) || l <= 0) throw new ArgumentException($"the rotation ({q.X} {q.Y} {q.Z} {q.W}) has no direction");
        return Quaternion.Normalize(q);
    }

    /// <summary>A position value: three floats, metres.</summary>
    public static byte[] Position12(Vector3 p)
    {
        var v = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(v, p.X);
        BinaryPrimitives.WriteSingleLittleEndian(v.AsSpan(4), p.Y);
        BinaryPrimitives.WriteSingleLittleEndian(v.AsSpan(8), p.Z);
        return v;
    }

    /// <summary>A rotation curve in the 6-byte form, tag 0x0C or 0x0D (alignment 2), at any key count.</summary>
    public static byte[] RotCurve(IReadOnlyList<int> ticks, IReadOnlyList<Quaternion> q, bool? twoByteTimes = null) =>
        CurveN(Rotation6, 2, ticks, Values(q, Encode6), twoByteTimes);

    /// <summary>A rotation curve in the 12-byte form, tag 0x18 or 0x19 (alignment 4), at any key count.</summary>
    public static byte[] RotCurve12(IReadOnlyList<int> ticks, IReadOnlyList<Quaternion> q, bool? twoByteTimes = null) =>
        CurveN(Rotation12, 4, ticks, Values(q, x => Encode12(x)), twoByteTimes);

    /// <summary>A position curve, tag 0x1B or 0x1C (alignment 4), at any key count.</summary>
    public static byte[] PosCurve(IReadOnlyList<int> ticks, IReadOnlyList<Vector3> p, bool? twoByteTimes = null) =>
        CurveN(Position, 4, ticks, Values(p, Position12), twoByteTimes);

    /// <summary>A byte track's curve, tag 0x27 or 0x28 (one byte a value, alignment 1), at any key count.</summary>
    public static byte[] ByteCurve(IReadOnlyList<int> ticks, IReadOnlyList<byte> v, bool? twoByteTimes = null) =>
        CurveN(ByteTrack, 1, ticks, Values(v, x => new[] { x }), twoByteTimes);

    /// <summary>A foot plant curve, tag 0x42 or 0x43 (alignment 8): a key at each landing, the first at tick 0 (all four values 0 where the
    /// foot is in the air then). A key's value is four signed 16-bit numbers: the plant's length in ticks, then the point's height, y and x in
    /// mm (<see cref="FootPlant.Point"/> is x lateral, y forward, z height, in metres).</summary>
    public static byte[] PlantCurve(IReadOnlyList<FootPlant> keys, bool? twoByteTimes = null)
    {
        var vals = new byte[keys.Count][];
        for (int i = 0; i < keys.Count; i++)
        {
            var (_, len, p) = keys[i];
            var v = new byte[8];
            BinaryPrimitives.WriteInt16LittleEndian(v, Short(len, "the plant's length"));
            BinaryPrimitives.WriteInt16LittleEndian(v.AsSpan(2), Short(MathF.Round(p.Z * 1000), "the plant's height"));
            BinaryPrimitives.WriteInt16LittleEndian(v.AsSpan(4), Short(MathF.Round(p.Y * 1000), "the plant's y"));
            BinaryPrimitives.WriteInt16LittleEndian(v.AsSpan(6), Short(MathF.Round(p.X * 1000), "the plant's x"));
            vals[i] = v;
        }
        return CurveN(Plants, 8, keys.Select(k => k.Tick).ToList(), vals, twoByteTimes);
    }

    static short Short(float v, string what) =>
        float.IsFinite(v) && v >= short.MinValue && v <= short.MaxValue ? (short)v : throw new ArgumentException($"{what} {v} is outside a signed 16-bit value");

    static byte[][] Values<T>(IReadOnlyList<T> xs, Func<T, byte[]> f)
    {
        var v = new byte[xs.Count][];
        for (int i = 0; i < v.Length; i++) v[i] = f(xs[i]);
        return v;
    }
}

/// <summary>Which ticks to key when a long clip is written: every ceil(lastTick / 254) ticks, at least every 2, with the last tick always
/// keyed. So no curve passes 255 keys, and the one-byte-count forms the game plays everywhere suffice.</summary>
public static class KeyingRule
{
    /// <summary>The step between keys for a clip of <paramref name="lastTick"/> ticks.</summary>
    public static int Step(int lastTick) => Math.Max(2, (lastTick + 253) / 254);

    /// <summary>The keyed ticks from 0 to <paramref name="lastTick"/> (0 gives the single key 0). At most 255.</summary>
    public static int[] Ticks(int lastTick)
    {
        if (lastTick < 0) throw new ArgumentException($"a clip of {lastTick} ticks");
        int s = Step(lastTick);
        var l = new List<int>(Math.Min(256, lastTick / s + 2));
        for (int t = 0; t < lastTick; t += s) l.Add(t);
        l.Add(lastTick);
        return [.. l];
    }
}
