using System.Buffers.Binary;
using GrbAnimTool.Core.Oodle;

namespace GrbAnimTool.Core.Datafiles;

/// <summary>One chunk of a TOC block: its uncompressed and stored sizes, the check word before its bytes, where its bytes start in the
/// payload, and whether it is compressed (stored and uncompressed sizes differ).</summary>
public readonly record struct CfdChunk(uint Uncompressed, uint Stored, uint Prefix, int DataOffset, bool Compressed);

/// <summary>One CompressedFileData block in TOC form.</summary>
public sealed class CfdBlock
{
    public int Offset;
    public short Version;
    public byte Algorithm;
    public uint BlockSizeToc;
    public bool Toc => (BlockSizeToc >> 31) == 1;
    public List<CfdChunk> Chunks = [];
    public int End;
    public long UncompressedLength => Chunks.Sum(c => (long)c.Uncompressed);
}

/// <summary>The compressed container a datafile payload is stored in: one or more TOC blocks, each a header, a chunk count, the chunks'
/// sizes, then each chunk's check word and bytes. Chunks are stored as they are or compressed with Oodle. Read only.</summary>
public static class CompressedFileData
{
    public const ulong Magic = 0x1004FA9957FBAA33UL;
    public const int ChunkSize = 0x8000;

    /// <summary>What one datafile may decode to: less than 1 GiB in all its blocks (the largest datafile of the game decodes to far less).</summary>
    public const long MaxDecoded = 1L << 30;

    /// <summary>The most uncompressed bytes a compressed chunk may claim per byte it stores. The highest in the game's forges is about 5,461
    /// (a full 0x8000 chunk in 6 bytes); a chunk claiming more is refused as a damaged file.</summary>
    public const int MaxChunkRatio = 16384;

    public static bool StartsBlock(ReadOnlySpan<byte> p, int at) =>
        at >= 0 && at + 8 <= p.Length && BinaryPrimitives.ReadUInt64LittleEndian(p[at..]) == Magic;

    /// <summary>Reads one TOC block: u32 N, N x (u32 uncompressed, u32 stored), N x (u32 check word, stored bytes). The non-TOC form is
    /// refused before any of its sizes is read: datafile payloads never use it. Every size is checked before anything is allocated from it.</summary>
    public static CfdBlock ReadBlock(ReadOnlySpan<byte> p, int at, int end, long limit = MaxDecoded)
    {
        if (!StartsBlock(p, at)) throw new InvalidDataException($"no CompressedFileData magic at +{at}");
        if (at + 15 > end) throw new InvalidDataException("block header truncated");
        var b = new CfdBlock
        {
            Offset = at,
            Version = BinaryPrimitives.ReadInt16LittleEndian(p[(at + 8)..]),
            Algorithm = p[at + 10],
            BlockSizeToc = BinaryPrimitives.ReadUInt32LittleEndian(p[(at + 11)..]),
        };
        int q = at + 15;
        if (!b.Toc) throw new InvalidDataException($"non-TOC CompressedFileData block at +{at}: not accepted (datafiles use TOC blocks)");
        // 3 (Oodle) is the only algorithm byte of the game's TOC blocks; another value is refused here rather than failing later inside Oodle
        if (b.Algorithm != 3) throw new InvalidDataException($"CompressedFileData block at +{at}: unknown algorithm byte {b.Algorithm} (only 3, Oodle, is known)");
        if (q + 4 > end) throw new InvalidDataException("chunk count truncated");
        int n = BinaryPrimitives.ReadInt32LittleEndian(p[q..]); q += 4;
        if (n <= 0 || n > 1_000_000 || q + 8L * n > end) throw new InvalidDataException($"implausible chunk count {n}");
        var u = new uint[n]; var c = new uint[n];
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            u[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[q..]); c[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[(q + 4)..]); q += 8;
            // every chunk holds 1..0x8000 uncompressed bytes, and a block decodes to less than its limit
            if (u[i] == 0 || u[i] > ChunkSize) throw new InvalidDataException($"chunk {i} claims {u[i]} uncompressed bytes (allowed 1..{ChunkSize})");
            if (c[i] == 0) throw new InvalidDataException($"chunk {i} stores 0 bytes for {u[i]} uncompressed bytes");
            if (u[i] != c[i] && u[i] > (ulong)MaxChunkRatio * c[i])
                throw new InvalidDataException($"chunk {i} claims {u[i]} uncompressed bytes for {c[i]} stored byte(s) (more than {MaxChunkRatio} per stored byte): a damaged file");
            total += u[i];
            if (total >= limit) throw new InvalidDataException("block claims 1 GiB or more of uncompressed data");
        }
        for (int i = 0; i < n; i++)
        {
            if (q + 4L + c[i] > end) throw new InvalidDataException($"chunk {i} overruns the payload");
            uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(p[q..]);
            b.Chunks.Add(new CfdChunk(u[i], c[i], prefix, q + 4, u[i] != c[i]));
            q += 4 + (int)c[i];
        }
        b.End = q;
        return b;
    }

    /// <summary>Consecutive TOC blocks from offset 0. Bytes after the last block that do not start a block are a trailer;
    /// <paramref name="trailerOffset"/> is where it starts (the payload's length if there is none).</summary>
    public static List<CfdBlock> ReadBlocks(ReadOnlySpan<byte> payload, out int trailerOffset)
    {
        var list = new List<CfdBlock>();
        int at = 0;
        while (at < payload.Length && StartsBlock(payload, at))
        {
            var b = ReadBlock(payload, at, payload.Length);
            list.Add(b);
            at = b.End;
        }
        if (list.Count == 0) throw new InvalidDataException("payload does not start with a CompressedFileData block");
        trailerOffset = at;
        // the whole datafile decodes to less than 1 GiB: summed over every block before anything is allocated
        long claim = list.Sum(b => b.UncompressedLength);
        if (claim >= MaxDecoded) throw new InvalidDataException($"its blocks claim {claim} decoded bytes in all (1 GiB or more): a damaged file");
        return list;
    }

    /// <summary>Decodes a block's chunks in order, each into its own array allocated when that chunk is reached, so what a block claims costs
    /// memory only for the chunks that really decode. Returns the number of bytes decoded.</summary>
    internal static long DecodeChunks(ReadOnlySpan<byte> payload, CfdBlock b, OodleLibrary? oodle, List<byte[]> into)
    {
        long made = 0;
        for (int i = 0; i < b.Chunks.Count; i++)
        {
            var d = DecodeChunk(payload, b, i, oodle);
            into.Add(d);
            made += d.Length;
        }
        return made;
    }

    /// <summary>Chunk <paramref name="i"/> of a block, decoded to exactly the bytes it claims. Stored chunks are copied and never passed to
    /// Oodle; a compressed chunk that decodes to another size is refused as a damaged file.</summary>
    internal static byte[] DecodeChunk(ReadOnlySpan<byte> payload, CfdBlock b, int i, OodleLibrary? oodle)
    {
        var c = b.Chunks[i];
        var src = payload.Slice(c.DataOffset, (int)c.Stored);
        if (!c.Compressed) return src.ToArray();
        if (oodle is null) throw new InvalidOperationException("compressed chunk but no Oodle library loaded");
        var dst = new byte[c.Uncompressed];
        try { oodle.Decompress(src, dst); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"chunk {i} of the block at +{b.Offset} does not decode to the {c.Uncompressed} bytes it claims ({ex.Message}): a damaged file");
        }
        return dst;
    }

    /// <summary>The decoded chunks as one array of <paramref name="total"/> bytes, allocated once they have all decoded.</summary>
    internal static byte[] Join(List<byte[]> parts, long total)
    {
        var all = new byte[total];
        long o = 0;
        foreach (var p in parts) { p.CopyTo(all, o); o += p.Length; }
        return all;
    }
}
