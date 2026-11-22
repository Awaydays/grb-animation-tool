using System.Buffers.Binary;
using System.Text;

namespace GrbAnimTool.Core.Export;

/// <summary>The glTF 2.0 binary container (.glb): a 12-byte header (magic "glTF", version 2, the file's length), a JSON chunk padded with
/// spaces to 4 bytes, then a binary chunk padded with zeros to 4 bytes. Each chunk is a u32 length, a u32 type and its data.</summary>
public static class Glb
{
    public const uint Magic = 0x46546C67;       // "glTF"
    public const uint JsonChunk = 0x4E4F534A;   // "JSON"
    public const uint BinChunk = 0x004E4942;    // "BIN\0"

    /// <summary>A .glb of the JSON and the binary buffer (none when empty).</summary>
    public static byte[] Write(byte[] json, ReadOnlySpan<byte> bin)
    {
        int jl = Pad4(json.Length), bl = Pad4(bin.Length);
        long total = 12 + 8 + jl + (bin.Length > 0 ? 8 + bl : 0);
        if (total > int.MaxValue) throw new InvalidOperationException($"a .glb of {total} B is too large");
        var b = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)jl);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), JsonChunk);
        json.CopyTo(b, 20);
        b.AsSpan(20 + json.Length, jl - json.Length).Fill((byte)' ');
        if (bin.Length > 0)
        {
            int at = 20 + jl;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)bl);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), BinChunk);
            bin.CopyTo(b.AsSpan(at + 8));      // the padding is the array's own zeros
        }
        return b;
    }

    static int Pad4(int n) => (n + 3) & ~3;

    /// <summary>The JSON text and the binary chunk of a .glb (an empty binary chunk when it has none). Throws
    /// <see cref="InvalidDataException"/> unless the header, the lengths, the chunk types and the 4-byte alignment are as the format
    /// says.</summary>
    public static (string Json, ReadOnlyMemory<byte> Bin) Read(byte[] b)
    {
        if (b.Length < 20) throw new InvalidDataException($"a .glb of {b.Length} B is shorter than its header");
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) throw new InvalidDataException("not a .glb: the magic isn't \"glTF\"");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if (version != 2) throw new InvalidDataException($"glTF binary version {version}, not 2");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        if (length != b.Length) throw new InvalidDataException($"the header's length {length} isn't the file's {b.Length} B");
        if (b.Length % 4 != 0) throw new InvalidDataException("the file's length isn't a multiple of 4");
        uint jl = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)) != JsonChunk) throw new InvalidDataException("the first chunk isn't JSON");
        if (jl % 4 != 0) throw new InvalidDataException($"the JSON chunk's length {jl} isn't a multiple of 4");
        if (jl > b.Length - 20) throw new InvalidDataException($"the JSON chunk of {jl} B runs past the file's end");
        string json = Encoding.UTF8.GetString(b, 20, (int)jl);
        long at = 20 + jl;
        if (at == b.Length) return (json, ReadOnlyMemory<byte>.Empty);
        if (at + 8 > b.Length) throw new InvalidDataException("a chunk header runs past the file's end");
        uint bl = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at));
        if (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at + 4)) != BinChunk) throw new InvalidDataException("the second chunk isn't BIN");
        if (bl % 4 != 0) throw new InvalidDataException($"the BIN chunk's length {bl} isn't a multiple of 4");
        if (bl > b.Length - at - 8) throw new InvalidDataException($"the BIN chunk of {bl} B runs past the file's end");
        // chunks of other types may follow; the format says a reader skips them, so only their lengths are checked
        for (long q = at + 8 + bl; q < b.Length;)
        {
            if (q + 8 > b.Length) throw new InvalidDataException("a chunk header runs past the file's end");
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)q));
            if (n % 4 != 0 || n > b.Length - q - 8) throw new InvalidDataException($"a chunk of {n} B at +{q:X} doesn't fit the file");
            q += 8 + n;
        }
        return (json, new ReadOnlyMemory<byte>(b, (int)at + 8, (int)bl));
    }
}
