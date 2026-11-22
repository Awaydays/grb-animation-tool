using System.Buffers.Binary;
using System.Text;
using GrbAnimTool.Core.Oodle;

namespace GrbAnimTool.Core.Datafiles;

/// <summary>One datafile record: u32 type, u32 length, u32 name length, the name, a byte, then the body. <see cref="Bytes"/> is the whole
/// record, a view into the decoded datafile (not a copy).</summary>
public sealed class DatafileRecord
{
    /// <summary>The record's place in the datafile's table, from 0. A record file shared for this record is named by it.</summary>
    public int Index;
    public ulong Id;
    public uint Type;
    public string Name = "";
    public ReadOnlyMemory<byte> Bytes;

    /// <summary>The byte after the name is not 0 (animation records, type 0x0FA3067F, have 1 there). Such a record's length field does not
    /// satisfy 13 + name length + length = size; it is delimited by the table's size only.</summary>
    public bool Opaque;
}

/// <summary>Datafile decode: the TOC blocks decompressed and joined, then the table and records parsed. Block 1 holds the table; block 2
/// onwards the records.</summary>
public static class DatafileCodec
{
    /// <summary>Decompresses all TOC blocks of a datafile payload, chunk by chunk: memory grows only as chunks decode, never by what the
    /// blocks claim. <paramref name="block1Length"/> is block 1's uncompressed length (the table).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> payload, OodleLibrary? oodle, out int block1Length, out int trailerOffset)
    {
        var blocks = CompressedFileData.ReadBlocks(payload, out trailerOffset);
        var parts = new List<byte[]>(blocks.Sum(b => b.Chunks.Count));
        long made = 0;
        block1Length = 0;
        for (int k = 0; k < blocks.Count; k++)
        {
            made += CompressedFileData.DecodeChunks(payload, blocks[k], oodle, parts);
            if (k == 0) block1Length = checked((int)made);
        }
        return CompressedFileData.Join(parts, made);
    }

    /// <summary>Strict parse: the table is u16 count + count x (u64 id, u32 size, u16 0), and the records tile the rest exactly. A record whose
    /// byte after the name is 0 must satisfy 13 + name length + length = size and carry the table's ID at the start of its body; a record with
    /// any other byte there is opaque, and only its name length and its place in the tiling are checked. Block 1 is the table, optionally
    /// followed by a trailer block (<see cref="IsTrailerBlock"/>) that is skipped; any other surplus is refused.</summary>
    public static List<DatafileRecord> Parse(byte[] decoded, int tableBytes)
    {
        ReadOnlySpan<byte> d = decoded;
        if (d.Length < 2 || tableBytes < 2 || tableBytes > d.Length) throw new InvalidDataException($"a datafile of {d.Length} B with a table of {tableBytes} B: too short");
        int n = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (2 + 14 * n != tableBytes && TrailerBlockLength(d, tableBytes) == 0) throw new InvalidDataException($"table size {2 + 14 * n} != block 1 length {tableBytes}");
        var list = new List<DatafileRecord>(n);
        int p = tableBytes;
        for (int i = 0; i < n; i++)
        {
            int t = 2 + 14 * i;
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(d[t..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d[(t + 8)..]);
            if (BinaryPrimitives.ReadUInt16LittleEndian(d[(t + 12)..]) != 0) throw new InvalidDataException($"table u16 != 0 at {i}");
            if (p + 13 > d.Length) throw new InvalidDataException($"record {i} header beyond end");
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
            uint nl = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 8)..]);
            if (p + (long)size > d.Length || 13L + nl > size) throw new InvalidDataException($"record {i}: size {size} beyond the end or smaller than its header");
            bool opaque = d[p + 12 + (int)nl] != 0;
            if (!opaque)
            {
                if (13L + nl + len != size) throw new InvalidDataException($"record {i}: size identity fails");
                if (len < 12) throw new InvalidDataException($"record {i}: body of {len} bytes is shorter than its ID + type (12)");
                if (BinaryPrimitives.ReadUInt64LittleEndian(d[(p + 13 + (int)nl)..]) != id) throw new InvalidDataException($"record {i}: body ID != table ID");
            }
            list.Add(new DatafileRecord
            {
                Index = i, Id = id, Type = type, Name = Encoding.ASCII.GetString(d.Slice(p + 12, (int)nl)),
                Bytes = new ReadOnlyMemory<byte>(decoded, p, (int)size), Opaque = opaque,
            });
            p += (int)size;
        }
        if (p != d.Length) throw new InvalidDataException("records do not tile the datafile");
        return list;
    }

    /// <summary>Extra bytes block 1 may carry after the table: at least <see cref="MinTrailerBlock"/> B, the first 0x5D, the u32 12 B before
    /// their end equal to their own length. None of the game's datafiles has them; a datafile not written by the game may carry them. Nothing
    /// in them is read.</summary>
    public static bool IsTrailerBlock(ReadOnlySpan<byte> x) => x.Length >= MinTrailerBlock && x[0] == 0x5D && BinaryPrimitives.ReadUInt32LittleEndian(x[^12..]) == (uint)x.Length;

    /// <summary>The shortest such block: a 13-byte stream header and the closing u32 length and u64.</summary>
    public const int MinTrailerBlock = 25;

    /// <summary>The length of that block after the table in a decoded datafile's block 1, or 0 when block 1 holds none.</summary>
    public static int TrailerBlockLength(ReadOnlySpan<byte> d, int tableBytes)
    {
        if (d.Length < 2 || tableBytes > d.Length) return 0;
        long table = 2 + 14L * BinaryPrimitives.ReadUInt16LittleEndian(d);
        return table < tableBytes && IsTrailerBlock(d[(int)table..tableBytes]) ? tableBytes - (int)table : 0;
    }
}
