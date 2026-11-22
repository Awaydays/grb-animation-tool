using System.Buffers.Binary;

namespace GrbAnimTool.Core.Clips;

/// <summary>Reads the keys of the tracks that hold no rotation or position: byte tracks (form 13, tags 0x27 to 0x29, one unsigned byte a
/// key), float tracks (form 18, tags 0x36 to 0x38, a 4-byte float a key; in the helper tracks the float's bits are a bone hash), and any
/// other inline form's values as the bytes stored. Every read is checked against the curve's key count and its source's end: a key outside
/// either gives null. A reference curve must be resolved first (<see cref="SharedCurveTable.Resolve"/>).</summary>
public static class ScalarDecoder
{
    /// <summary>Whether the curve holds byte values (form 13).</summary>
    public static bool IsByte(Curve c) => !c.IsReference && c.Form == 13;

    /// <summary>Whether the curve holds float values (form 18).</summary>
    public static bool IsFloat(Curve c) => !c.IsReference && c.Form == 18;

    static bool Fits(Curve c, int key) =>
        !c.IsReference && key >= 0 && key < c.Keys && c.ValueBytes > 0 && c.ValuesAt >= 0
        && c.ValuesAt + (long)(key + 1) * c.ValueBytes <= c.Source.Length;

    /// <summary>A byte key (form 13). Null for any other form or a key outside the curve.</summary>
    public static byte? KeyByte(Curve c, int key) => IsByte(c) && Fits(c, key) ? c.Source[c.ValuesAt + key] : null;

    /// <summary>A float key (form 18). Null for any other form or a key outside the curve.</summary>
    public static float? KeyFloat(Curve c, int key) =>
        IsFloat(c) && Fits(c, key) ? BinaryPrimitives.ReadSingleLittleEndian(c.Source.AsSpan(c.ValuesAt + key * 4)) : null;

    /// <summary>A float key's bits (form 18): where a helper track names a bone, the bone's hash. Null for any other form or a key outside
    /// the curve.</summary>
    public static uint? KeyFloatBits(Curve c, int key) =>
        IsFloat(c) && Fits(c, key) ? BinaryPrimitives.ReadUInt32LittleEndian(c.Source.AsSpan(c.ValuesAt + key * 4)) : null;

    /// <summary>A key's value as stored, for any inline form of a known value size (forms 14 and 15 among them, whose values no reader
    /// decodes). Null for a key outside the curve.</summary>
    public static byte[]? KeyRaw(Curve c, int key) => Fits(c, key) ? c.Source.AsSpan(c.ValuesAt + key * c.ValueBytes, c.ValueBytes).ToArray() : null;

    /// <summary>Every byte key of a form-13 curve, or null for another form or a curve whose values don't fit its source.</summary>
    public static byte[]? Bytes(Curve c)
    {
        if (!IsByte(c) || c.Keys == 0 || !Fits(c, (int)Math.Min(c.Keys - 1, int.MaxValue))) return null;
        return c.Source.AsSpan(c.ValuesAt, (int)c.Keys).ToArray();
    }

    /// <summary>Every float key of a form-18 curve, or null for another form or a curve whose values don't fit its source.</summary>
    public static float[]? Floats(Curve c)
    {
        if (!IsFloat(c) || c.Keys == 0 || !Fits(c, (int)Math.Min(c.Keys - 1, int.MaxValue))) return null;
        var f = new float[c.Keys];
        for (int k = 0; k < f.Length; k++) f[k] = BinaryPrimitives.ReadSingleLittleEndian(c.Source.AsSpan(c.ValuesAt + k * 4));
        return f;
    }

    /// <summary>The curve's bytes as stored (tag to its last value), or null when they don't lie inside its source.</summary>
    public static byte[]? Stored(Curve c) =>
        c.At >= 0 && c.DiskLength > 0 && c.At + (long)c.DiskLength <= c.Source.Length ? c.Source.AsSpan(c.At, c.DiskLength).ToArray() : null;
}
