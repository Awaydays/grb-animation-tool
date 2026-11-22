namespace GrbAnimTool.Core.Clips;

/// <summary>What a track's curve holds, by its tag's kind byte.</summary>
public enum TrackKind
{
    Rotation = 0,
    Position = 2,
    /// <summary>Kind 4 (tags 0x2D to 0x35): not read.</summary>
    Kind4 = 4,
    /// <summary>Kind 5 (tags 0x36 to 0x3E): form 18 holds a 4-byte value a key (a float; in the helper tracks a hash). Sized by the
    /// walker; the core has no reader for the values yet.</summary>
    Float = 5,
    /// <summary>Foot plants (tags 0x3F to 0x47); form 22 is read by <see cref="CurveDecoder.Plant"/>.</summary>
    Plant = 6,
    /// <summary>Kind 7 (tags 0x24 to 0x2C): form 13 holds one byte a key. Sized by the walker; the core has no reader for the values yet.</summary>
    Byte = 7,
    Unknown = -1,
}

/// <summary>A curve as stored: inline (a tag, u32 size in memory, u32 key count, the key times, then the values) or a reference into the
/// shared curve table (tag 0xFE + u32 index, from 1).
/// <para>A tag's form is tag / 3 and fixes the value's size; tag % 3 says how the key times stand: 0 one byte per key after the first, 1 two
/// bytes, 2 a bitmap (one byte n | shift &lt;&lt; 6, then n bytes; a key at each set bit, the bit's number shifted left). The first key's
/// time is not stored: it is tick 0. Times are ticks of 1/60 s.</para></summary>
/// <param name="At">The tag's offset in <paramref name="Source"/>.</param>
/// <param name="Ref">The shared-table index of a reference (tag 0xFE), else 0.</param>
/// <param name="DiskLength">The curve's length as stored.</param>
/// <param name="ValuesAt">Where the first key's value starts.</param>
/// <param name="ValueBytes">Bytes per value.</param>
/// <param name="Source">The record (or shared table) the curve lies in; every offset is into it.</param>
/// <param name="BitmapBytes">The bitmap's length for a bitmap time form, else 0.</param>
/// <param name="BitmapShift">The bitmap's shift, else 0.</param>
/// <param name="TimesAt">Where the times start (for a bitmap, its count byte).</param>
public sealed record Curve(int At, byte Tag, uint Size, uint Keys, uint Ref, int DiskLength, int ValuesAt, int ValueBytes, byte[] Source,
    int BitmapBytes, int BitmapShift, int TimesAt)
{
    public const byte ReferenceTag = 0xFE;

    public bool IsReference => Tag == ReferenceTag;

    /// <summary>The tag's form (tag / 3): it fixes the value's layout.</summary>
    public int Form => Tag / 3;

    public TrackKind Kind => CurveTags.KindOf(Tag);
}

/// <summary>The game's table of curve tags.</summary>
public static class CurveTags
{
    /// <summary>Bytes per value by form; -1 where no curve of this form has been met.</summary>
    public static int ValueBytes(byte tag) => (tag / 3) switch
    {
        0 => 16, 1 => 2, 2 => 3, 3 => 4, 4 => 6, 5 => 8, 6 => 1, 7 => 2, 8 => 12, 9 => 12, 11 => 6,
        13 => 1, 14 => 2, 15 => 1, 16 => 1, 18 => 4, 19 => 4, 22 => 8,
        _ => -1,
    };

    public static bool IsRotation(byte tag) => tag <= 0x1A;
    public static bool IsPosition(byte tag) => tag is >= 0x1B and <= 0x23;

    /// <summary>The kind byte by tag: tags up to 0x1A rotations, 0x1B to 0x23 positions, 0x24 to 0x2C kind 7, 0x2D to 0x35 kind 4, 0x36 to
    /// 0x3E kind 5, 0x3F to 0x47 kind 6.</summary>
    public static TrackKind KindOf(byte tag) => tag switch
    {
        <= 0x1A => TrackKind.Rotation,
        <= 0x23 => TrackKind.Position,
        <= 0x2C => TrackKind.Byte,
        <= 0x35 => TrackKind.Kind4,
        <= 0x3E => TrackKind.Float,
        <= 0x47 => TrackKind.Plant,
        _ => TrackKind.Unknown,
    };
}
