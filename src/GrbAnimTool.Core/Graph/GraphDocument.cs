using System.Buffers.Binary;
using GrbAnimTool.Core.Datafiles;
using GrbAnimTool.Core.IO;
using GrbAnimTool.Core.Lzo;

namespace GrbAnimTool.Core.Graph;

/// <summary>One row of a document's class table: the class's u32, and how many objects of it the tokens name.</summary>
public sealed record GraphClassRow(int Index, int At, uint Class, int Count);

/// <summary>One token of a document's token stream: a kind and a number; kind 0x40 adds a class index (n objects of that class).</summary>
public readonly record struct GraphToken(int At, byte Kind, int N, int ClassIndex);

/// <summary>One object of a document's body: where its class's u32 stands, the class, whether it stands after a 16-byte ID (02 02 form)
/// rather than plain (02 00), and whether a token names it or it stands inline (a field holding it by value).</summary>
public readonly record struct GraphObject(int At, uint Class, bool WithId, bool Tokened);

/// <summary>A graph document whose record, stream, head or body doesn't read. The message names the document and what failed.</summary>
public sealed class GraphDocumentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An animation graph document: a record of the animation datafile holding an LZO1X stream (after the stream object 0xB5790717 and
/// its u32 length; in a document the u32 four bytes after the stream states the decoded length, while GR_Graph's stream is followed by its
/// clip list and is decoded to its end marker), decoded to the packed form:
/// <list type="bullet">
/// <item>the head: 09 01, a u32, a varint, the class count, each class's u32, a 16-byte word and its object count;</item>
/// <item>the pairs: each a token kind with the sum of its tokens' numbers; a zero; the token count; the token stream's length; the tokens;</item>
/// <item>the body: objects, each its class's u32 after 02 00, or after 02 02 and a 16-byte ID.</item>
/// </list>
/// <see cref="Read"/> checks the head's every count and sum against the tokens, and walks the body: every object a token names must stand in
/// the body in the tokens' order, so a document that reads has been read to its end.</summary>
public sealed class GraphDocument
{
    /// <summary>The record types of graph documents: the graph record GR_Graph, and the documents (state machines, layer and node graphs).</summary>
    public const uint GraphType = 0x3527D1AE, DocumentType = 0xB19D4FCD;
    /// <summary>The object that holds the stream in the record.</summary>
    public const uint StreamClass = 0xB5790717;
    /// <summary>The largest decoded document accepted (the game's largest is about 4.2 MB).</summary>
    public const int MaxDecodedLength = 64 << 20;
    /// <summary>The document's handle list. In a document it is the first object token's class; in GR_Graph the first object token is of
    /// another class (0x59E76516) and this class's table count is 0. See <see cref="Walk"/> for the first token's rule.</summary>
    public const uint HandleListClass = 0x74E00B4C;

    public required ulong Id { get; init; }
    public required string Name { get; init; }
    public required uint Type { get; init; }
    /// <summary>The decoded stream.</summary>
    public required byte[] Bytes { get; init; }
    /// <summary>Where the stream stands in the record, its length, and the record's bytes after it.</summary>
    public required int StreamAt { get; init; }
    public required int StreamLength { get; init; }
    public required ReadOnlyMemory<byte> Record { get; init; }

    public required IReadOnlyList<GraphClassRow> Classes { get; init; }
    public required IReadOnlyList<GraphToken> Tokens { get; init; }
    /// <summary>The first byte of the body (right after the tokens).</summary>
    public required int BodyAt { get; init; }
    /// <summary>The body's objects in file order, as the walker found them.</summary>
    public required IReadOnlyList<GraphObject> Objects { get; init; }
    public int TokenedObjects => Objects.Count(o => o.Tokened);
    public int InlineObjects => Objects.Count(o => !o.Tokened);

    /// <summary>The record's bytes after the stream (its tail: the stated length, the children's record IDs, and in GR_Graph the clip list).</summary>
    public ReadOnlyMemory<byte> Tail => Record[(StreamAt + StreamLength)..];

    /// <summary>The index in <see cref="Objects"/> of the object at or before <paramref name="at"/>, or -1 before the first.</summary>
    public int ObjectAtOrBefore(int at)
    {
        int lo = 0, hi = Objects.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Objects[mid].At <= at) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    /// <summary>The end of object <paramref name="i"/>'s own bytes: where the next object's 02 00 (or 02 02 and ID) starts, or the body's end.</summary>
    public int ObjectEnd(int i) => i + 1 < Objects.Count ? Objects[i + 1].At - (Objects[i + 1].WithId ? 18 : 2) : Bytes.Length;

    public static bool IsDocumentType(uint type) => type is GraphType or DocumentType;

    /// <summary>Reads a datafile record as a graph document. Throws <see cref="GraphDocumentException"/> when the record holds no stream, the
    /// stream doesn't decode to its stated length, or the head, tokens or body don't read.</summary>
    public static GraphDocument Read(DatafileRecord rec) => Read(rec.Bytes, rec.Id, rec.Name, rec.Type);

    public static GraphDocument Read(ReadOnlyMemory<byte> record, ulong id, string name, uint type)
    {
        string who = $"graph document 0x{id:X16} '{name}'";
        try
        {
            var r = record.Span;
            int at = FindStream(r, out int len);
            if (at < 0) throw new InvalidDataException("the record holds no LZO1X stream");
            byte[] d;
            if (type == GraphType)
                d = Lzo1x.DecompressAtMost(r.Slice(at, len), MaxDecodedLength);   // GR_Graph's tail is its clip list: no stated length
            else
            {
                int stated = Bin.I32(r, at + len + 4L, "the decoded length after the stream");
                if (stated <= 0 || stated > MaxDecodedLength) throw new InvalidDataException($"the stated decoded length {stated} B is out of range");
                d = Lzo1x.Decompress(r.Slice(at, len), stated);
            }
            var (classes, tokens, bodyAt) = ReadHead(d);
            var objects = Walk(d, classes, tokens, bodyAt);
            return new GraphDocument
            {
                Id = id, Name = name, Type = type, Bytes = d, StreamAt = at, StreamLength = len, Record = record,
                Classes = classes, Tokens = tokens, BodyAt = bodyAt, Objects = objects,
            };
        }
        catch (InvalidDataException ex) { throw new GraphDocumentException($"{who}: {ex.Message}", ex); }
    }

    /// <summary>The stream's place in a record, or -1: the stream object's class within the record's first 4,096 bytes, a u32 length, and a stream
    /// that fits the record and ends in LZO1X's end marker 11 00 00.</summary>
    public static int FindStream(ReadOnlySpan<byte> rec, out int len)
    {
        len = 0;
        Span<byte> tag = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tag, StreamClass);
        int limit = Math.Min(rec.Length - 8, 4096);
        for (int at = 0; at < limit;)
        {
            int i = rec[at..Math.Min(rec.Length, limit + 4)].IndexOf(tag);
            if (i < 0) return -1;
            int o = at + i;
            int l = BinaryPrimitives.ReadInt32LittleEndian(rec[(o + 4)..]);
            if (l > 3 && o + 8L + l <= rec.Length && rec[o + 8 + l - 3] == 0x11 && rec[o + 8 + l - 2] == 0 && rec[o + 8 + l - 1] == 0) { len = l; return o + 8; }
            at = o + 1;
        }
        return -1;
    }

    /// <summary>A varint: 7 bits a byte, low first, at most 5 bytes, every byte inside the input.</summary>
    internal static int Varint(ReadOnlySpan<byte> d, ref int p, string what)
    {
        uint v = 0;
        for (int s = 0; s < 35; s += 7)
        {
            byte b = Bin.U8(d, p++, what);
            // the 5th byte carries bits 28-34: bits 32-34 don't fit a u32, so they are refused rather than lost
            if (s == 28 && (b & 0x70) != 0) throw new InvalidDataException($"{what} at +{p - 1:X}: a varint wider than 32 bits");
            v |= (uint)(b & 0x7F) << s;
            if ((b & 0x80) == 0)
            {
                if (v > int.MaxValue) throw new InvalidDataException($"{what} at +{p - 1:X}: {v} is out of range");
                return (int)v;
            }
        }
        throw new InvalidDataException($"{what} at +{p - 1:X}: a varint longer than 5 bytes");
    }

    /// <summary>The head and the tokens, every count and sum checked: the pairs' sums equal the tokens' sums by kind, every kind but 0x40 has
    /// its pair, each class's count equals its tokens' objects, and the tokens end where the stream's length says.</summary>
    internal static (List<GraphClassRow>, List<GraphToken>, int) ReadHead(ReadOnlySpan<byte> d)
    {
        if (d.Length < 8 || d[0] != 9 || d[1] != 1) throw new InvalidDataException("the document doesn't start with 09 01");
        int p = 6;
        Varint(d, ref p, "the head's varint");
        int nClasses = Varint(d, ref p, "the class count");
        if (nClasses > (d.Length - p) / 21) throw new InvalidDataException($"a class count of {nClasses} can't fit the document");
        var classes = new List<GraphClassRow>(nClasses);
        for (int i = 0; i < nClasses; i++)
        {
            int at = p;
            uint c = Bin.U32(d, p, "a class row");
            Bin.Need(d, p, 20, "a class row");
            p += 20;
            classes.Add(new GraphClassRow(i, at, c, Varint(d, ref p, "a class's object count")));
        }
        int nPairs = Varint(d, ref p, "the pair count");
        if (nPairs > 256) throw new InvalidDataException($"a pair count of {nPairs}");
        var pairs = new List<(byte Kind, long Sum)>(nPairs);
        for (int i = 0; i < nPairs; i++) { byte k = Bin.U8(d, p++, "a pair's kind"); pairs.Add((k, Varint(d, ref p, "a pair's sum"))); }
        int zero = Varint(d, ref p, "the zero after the pairs");
        if (zero != 0) throw new InvalidDataException($"expected a zero after the pairs, read {zero}");
        int nTokens = Varint(d, ref p, "the token count");
        int streamLen = Varint(d, ref p, "the token stream's length");
        int tokensEnd = p + streamLen;
        if (streamLen > d.Length - p || nTokens > streamLen / 2) throw new InvalidDataException($"{nTokens} tokens in {streamLen} B don't fit the document");
        var tokens = new List<GraphToken>(nTokens);
        var sums = new Dictionary<byte, long>();
        var objects = new long[nClasses];
        for (int i = 0; i < nTokens; i++)
        {
            int at = p;
            byte k = Bin.U8(d, p++, "a token's kind");
            int n = Varint(d, ref p, "a token's number");
            int c = -1;
            if (k == 0x40)
            {
                c = Varint(d, ref p, "a token's class index");
                if (c >= nClasses) throw new InvalidDataException($"a token at +{at:X} names class index {c} of {nClasses}");
                objects[c] += n;
            }
            if (p > tokensEnd) throw new InvalidDataException($"the tokens run past +{tokensEnd:X}, where the stream's length ends them");
            sums[k] = sums.GetValueOrDefault(k) + n;
            tokens.Add(new GraphToken(at, k, n, c));
        }
        if (p != tokensEnd) throw new InvalidDataException($"the tokens end at +{p:X}, the stream's length says +{tokensEnd:X}");
        foreach (var (k, s) in pairs)
            if (sums.GetValueOrDefault(k) != s) throw new InvalidDataException($"token kind {k:X2}: the head says {s}, the tokens {sums.GetValueOrDefault(k)}");
        foreach (var k in sums.Keys)
            if (k != 0x40 && !pairs.Any(x => x.Kind == k)) throw new InvalidDataException($"token kind {k:X2} isn't in the head's pairs");
        foreach (var c in classes)
            if (c.Count != objects[c.Index]) throw new InvalidDataException($"class 0x{c.Class:X8}: the table says {c.Count}, the tokens {objects[c.Index]}");
        return (classes, tokens, tokensEnd);
    }

    /// <summary>The body's objects against the tokens. Each place where a class of the table stands after 02 00 (or 02 02 and a 16-byte ID)
    /// is an object. A token of n objects of one class stands first and its objects' children follow it, so an object is either the next
    /// token's or one still owed by an earlier token. A class whose table count is below its count in the body also stands inline,
    /// untokened. The first object token's objects stand before the body, without the 02 00 form (a document's handle list, GR_Graph's
    /// 0x59E76516), so that token is used up before the walk and its class's table count, less that token's N, is what the body's tokens
    /// name of it. That is checked: the body may hold no more places of that class than that, so the token's own objects can't stand in the
    /// body read as inline ones. Every token must be used up and nothing owed at the end; any other object is an error.</summary>
    internal static List<GraphObject> Walk(byte[] d, List<GraphClassRow> classes, List<GraphToken> tokens, int bodyAt)
    {
        var index = new Dictionary<uint, GraphClassRow>();
        foreach (var c in classes) index.TryAdd(c.Class, c);
        var toks = tokens.Where(t => t.Kind == 0x40).ToList();
        var places = new List<(int At, uint Class, bool WithId)>();
        var inBody = new Dictionary<uint, int>();
        for (int o = bodyAt + 2; o + 4 <= d.Length; o++)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
            if (!index.ContainsKey(v)) continue;
            bool plain = d[o - 2] == 2 && d[o - 1] == 0;
            bool withId = o >= bodyAt + 18 && d[o - 18] == 2 && d[o - 17] == 2;
            if (!plain && !withId) continue;
            places.Add((o, v, !plain));
            inBody[v] = inBody.GetValueOrDefault(v) + 1;
        }
        var owed = new int[classes.Count];
        int next = toks.Count > 0 ? 1 : 0;   // the first object token: its objects stand before the body
        var bodyCount = classes.Select(c => c.Count).ToArray();
        if (toks.Count > 0)
        {
            var first = classes[toks[0].ClassIndex];
            bodyCount[first.Index] -= toks[0].N;
            int standing = inBody.GetValueOrDefault(first.Class);
            if (standing > bodyCount[first.Index])
                throw new InvalidDataException($"the first object token's class 0x{first.Class:X8} stands {standing} times in the body; its table count "
                    + $"{first.Count} less the token's {toks[0].N}, which stand before the body, allows {bodyCount[first.Index]}");
        }
        var objs = new List<GraphObject>(places.Count);
        foreach (var (at, c, withId) in places)
        {
            var row = index[c];
            if (next < toks.Count && toks[next].ClassIndex == row.Index)
            {
                owed[row.Index] += Math.Max(0, toks[next].N - 1);
                next++;
                objs.Add(new GraphObject(at, c, withId, true));
            }
            else if (owed[row.Index] > 0) { owed[row.Index]--; objs.Add(new GraphObject(at, c, withId, true)); }
            else if (bodyCount[row.Index] < inBody[c]) objs.Add(new GraphObject(at, c, withId, false));
            else throw new InvalidDataException($"+{at:X}: class 0x{c:X8} stands here; the next token wants "
                + (next < toks.Count ? $"0x{classes[toks[next].ClassIndex].Class:X8}" : "nothing"));
        }
        if (next != toks.Count) throw new InvalidDataException($"{toks.Count - next} object tokens unused at the body's end, the next wants 0x{classes[toks[next].ClassIndex].Class:X8}");
        if (owed.Any(v => v != 0)) throw new InvalidDataException("objects still owed at the body's end");
        return objs;
    }
}
