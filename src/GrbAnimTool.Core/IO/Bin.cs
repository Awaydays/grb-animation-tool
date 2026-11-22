using System.Buffers.Binary;

namespace GrbAnimTool.Core.IO;

/// <summary>Little-endian reads used by every format reader. The checked forms throw <see cref="InvalidDataException"/> when a read would
/// run past the end of its input, so a damaged file fails with a plain message instead of an index error.</summary>
internal static class Bin
{
    public static void ReadExactly(Stream s, long offset, Span<byte> buffer)
    {
        s.Seek(offset, SeekOrigin.Begin);
        s.ReadExactly(buffer);
    }

    public static byte[] ReadAt(Stream s, long offset, int count)
    {
        var b = new byte[count];
        ReadExactly(s, offset, b);
        return b;
    }

    /// <summary>Throws unless <paramref name="count"/> bytes at <paramref name="at"/> lie inside <paramref name="b"/>.</summary>
    public static void Need(ReadOnlySpan<byte> b, long at, long count, string what)
    {
        if (at < 0 || count < 0 || at > b.Length - count) throw new InvalidDataException($"{what}: {count} B at +{at:X} run past the end ({b.Length} B)");
    }

    public static byte U8(ReadOnlySpan<byte> b, long at, string what) { Need(b, at, 1, what); return b[(int)at]; }
    public static int I32(ReadOnlySpan<byte> b, long at, string what) { Need(b, at, 4, what); return BinaryPrimitives.ReadInt32LittleEndian(b[(int)at..]); }
    public static uint U32(ReadOnlySpan<byte> b, long at, string what) { Need(b, at, 4, what); return BinaryPrimitives.ReadUInt32LittleEndian(b[(int)at..]); }
    public static ulong U64(ReadOnlySpan<byte> b, long at, string what) { Need(b, at, 8, what); return BinaryPrimitives.ReadUInt64LittleEndian(b[(int)at..]); }
    public static float F32(ReadOnlySpan<byte> b, long at, string what) { Need(b, at, 4, what); return BinaryPrimitives.ReadSingleLittleEndian(b[(int)at..]); }
}
